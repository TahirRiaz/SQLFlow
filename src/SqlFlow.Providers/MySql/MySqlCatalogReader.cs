using System.Data.Common;
using System.Globalization;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.MySql;

/// <summary>
/// Reads the MySQL catalog over a supplied open connection through <c>information_schema</c>. MySQL has no
/// separate schema level (a schema IS a database), so databases and schemas list the same set and an object's
/// schema part addresses its database. The column NativeType is the full <c>COLUMN_TYPE</c> rendering
/// (<c>int unsigned</c>, <c>varchar(50)</c>, <c>decimal(10,2)</c>), which the MySQL type mapper translates.
/// User search values are always parameters.
/// </summary>
public sealed class MySqlCatalogReader : IProviderCatalogReader
{
    private static readonly string[] SystemDatabases = ["mysql", "information_schema", "performance_schema", "sys"];

    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.MySQL;

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection connection, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT SCHEMA_NAME, DEFAULT_COLLATION_NAME
            FROM information_schema.SCHEMATA
            ORDER BY SCHEMA_NAME;
            """;

        var databases = new List<DatabaseInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!query.IncludeSystem && SystemDatabases.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            databases.Add(new DatabaseInfo { Name = name, Collation = reader.IsDBNull(1) ? null : reader.GetString(1), State = "ONLINE" });
        }

        return databases;
    }

    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(DbConnection connection, string? database, CatalogQuery query, CancellationToken ct = default)
    {
        // MySQL schemas ARE databases; the schema level collapses to the database itself.
        var databases = await ListDatabasesAsync(connection, query, ct).ConfigureAwait(false);
        return databases
            .Where(d => database is null || string.Equals(d.Name, database, StringComparison.OrdinalIgnoreCase))
            .Select(d => new SchemaInfo { Name = d.Name })
            .ToList();
    }

    public async Task<CatalogPage<ObjectInfo>> ListObjectsAsync(DbConnection connection, ObjectScope scope, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var database = scope.Schema ?? scope.Database;
        var items = new List<ObjectInfo>();
        long total = 0;

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 0;
            command.CommandText = $"""
                SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, COALESCE(TABLE_ROWS, 0)
                FROM information_schema.TABLES
                WHERE (@db IS NULL OR TABLE_SCHEMA = @db)
                  AND TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
                  AND ((@tables = 1 AND TABLE_TYPE = 'BASE TABLE') OR (@views = 1 AND TABLE_TYPE = 'VIEW'))
                  AND (@like IS NULL OR TABLE_NAME LIKE CONCAT('%', @like, '%'))
                ORDER BY TABLE_SCHEMA, TABLE_NAME
                LIMIT {query.Limit.ToString(CultureInfo.InvariantCulture)} OFFSET {query.Offset.ToString(CultureInfo.InvariantCulture)};
                """;
            AddParameter(command, "@db", database);
            AddParameter(command, "@tables", query.IncludeTables ? 1 : 0);
            AddParameter(command, "@views", query.IncludeViews ? 1 : 0);
            AddParameter(command, "@like", query.NameLike);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new ObjectInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Type = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase) ? ObjectType.View : ObjectType.Table,
                    ApproxRows = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                });
            }
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandTimeout = 0;
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.TABLES
                WHERE (@db IS NULL OR TABLE_SCHEMA = @db)
                  AND TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
                  AND ((@tables = 1 AND TABLE_TYPE = 'BASE TABLE') OR (@views = 1 AND TABLE_TYPE = 'VIEW'))
                  AND (@like IS NULL OR TABLE_NAME LIKE CONCAT('%', @like, '%'));
                """;
            AddParameter(countCommand, "@db", database);
            AddParameter(countCommand, "@tables", query.IncludeTables ? 1 : 0);
            AddParameter(countCommand, "@views", query.IncludeViews ? 1 : 0);
            AddParameter(countCommand, "@like", query.NameLike);
            total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return new CatalogPage<ObjectInfo> { Items = items, Offset = query.Offset, Limit = query.Limit, Total = total };
    }

    public async Task<IReadOnlyList<ObjectMatch>> SearchObjectsAsync(DbConnection connection, string? database, string term, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM information_schema.TABLES
            WHERE (@db IS NULL OR TABLE_SCHEMA = @db)
              AND TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
              AND TABLE_NAME LIKE CONCAT('%', @term, '%')
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            LIMIT 100;
            """;
        AddParameter(command, "@db", database);
        AddParameter(command, "@term", term);

        var matches = new List<ObjectMatch>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            matches.Add(new ObjectMatch
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                Type = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase) ? ObjectType.View : ObjectType.Table,
            });
        }

        return matches;
    }

    public async Task<CatalogObject?> IntrospectObjectAsync(DbConnection connection, ThreePartName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);

        // The object's "schema" part addresses the MySQL database; an empty one means the connection's default.
        var database = string.IsNullOrEmpty(name.Schema) ? null : name.Schema;

        string? tableType = null;
        await using (var typeCommand = connection.CreateCommand())
        {
            typeCommand.CommandTimeout = 0;
            typeCommand.CommandText = """
                SELECT TABLE_TYPE FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = COALESCE(@db, DATABASE()) AND TABLE_NAME = @name;
                """;
            AddParameter(typeCommand, "@db", database);
            AddParameter(typeCommand, "@name", name.Name);
            tableType = await typeCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (tableType is null)
        {
            return null;
        }

        var columns = new List<CatalogColumn>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 0;
            command.CommandText = """
                SELECT c.COLUMN_NAME, c.ORDINAL_POSITION, c.COLUMN_TYPE, c.IS_NULLABLE, c.COLLATION_NAME,
                       c.EXTRA, c.COLUMN_DEFAULT, (c.COLUMN_KEY = 'PRI') AS IS_PK
                FROM information_schema.COLUMNS c
                WHERE c.TABLE_SCHEMA = COALESCE(@db, DATABASE()) AND c.TABLE_NAME = @name
                ORDER BY c.ORDINAL_POSITION;
                """;
            AddParameter(command, "@db", database);
            AddParameter(command, "@name", name.Name);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var extra = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                columns.Add(new CatalogColumn
                {
                    Name = reader.GetString(0),
                    Ordinal = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    NativeType = reader.GetString(2),
                    IsNullable = string.Equals(reader.GetString(3), "YES", StringComparison.OrdinalIgnoreCase),
                    Collation = reader.IsDBNull(4) ? null : reader.GetString(4),
                    IsIdentity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    DefaultExpression = reader.IsDBNull(6) ? null : reader.GetString(6),
                    IsPrimaryKeyMember = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
                });
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var isView = string.Equals(tableType, "VIEW", StringComparison.OrdinalIgnoreCase);
        var (definition, availability) = isView
            ? await ReadViewDefinitionAsync(connection, name, ct).ConfigureAwait(false)
            : (null, DefinitionAvailability.NotApplicable);

        return new CatalogObject
        {
            Name = name,
            Type = isView ? ObjectType.View : ObjectType.Table,
            Columns = columns,
            Definition = definition,
            DefinitionAvailability = availability,
        };
    }

    /// <summary>
    /// Reads a view's SQL from <c>information_schema.VIEWS</c>.
    /// <para>MySQL does not refuse an unprivileged read here: without SHOW VIEW on the object it returns the row
    /// with VIEW_DEFINITION as an empty string. That empty answer is therefore reported as denied rather than as
    /// a view without a body, so an operator is told to ask for the grant instead of doubting the view.</para>
    /// </summary>
    private static async Task<(string? Definition, DefinitionAvailability Availability)> ReadViewDefinitionAsync(
        DbConnection connection, ThreePartName name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT VIEW_DEFINITION
            FROM information_schema.VIEWS
            WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name;
            """;
        AddParameter(command, "@schema", name.Schema);
        AddParameter(command, "@name", name.Name);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            // No row at all: the view is not visible to this login, which is the same practical answer.
            return (null, DefinitionAvailability.PermissionDenied);
        }

        var definition = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(definition)
            ? (null, DefinitionAvailability.PermissionDenied)
            : (definition, DefinitionAvailability.Available);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
