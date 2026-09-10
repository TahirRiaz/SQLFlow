using System.Data.Common;
using System.Globalization;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.Postgres;

/// <summary>
/// Reads the PostgreSQL catalog over a supplied open connection through <c>information_schema</c>. A
/// connection is pinned to one database, so the database listing returns the current database only (PostgreSQL
/// has no cross-database queries). The column NativeType is rendered from <c>udt_name</c> plus its modifiers
/// (<c>varchar(50)</c>, <c>numeric(10,2)</c>, <c>timestamptz(6)</c>), which the PostgreSQL type mapper
/// translates. Identity covers both declared identity columns and legacy serial defaults. User search values
/// are always parameters.
/// </summary>
public sealed class PostgresCatalogReader : IProviderCatalogReader
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.PostgreSQL;

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection connection, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = "SELECT current_database();";
        var name = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? string.Empty;
        return [new DatabaseInfo { Name = name, State = "ONLINE" }];
    }

    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(DbConnection connection, string? database, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT schema_name, schema_owner
            FROM information_schema.schemata
            WHERE ($1 = TRUE OR (schema_name NOT LIKE 'pg\_%' AND schema_name <> 'information_schema'))
            ORDER BY schema_name;
            """;
        AddParameter(command, query.IncludeSystem);

        var schemas = new List<SchemaInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            schemas.Add(new SchemaInfo { Name = reader.GetString(0), Owner = reader.IsDBNull(1) ? null : reader.GetString(1) });
        }

        return schemas;
    }

    public async Task<CatalogPage<ObjectInfo>> ListObjectsAsync(DbConnection connection, ObjectScope scope, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var items = new List<ObjectInfo>();
        long total;

        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = 0;
            command.CommandText = $"""
                SELECT t.table_schema, t.table_name, t.table_type,
                       COALESCE((SELECT c.reltuples::bigint FROM pg_catalog.pg_class c
                                 JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                                 WHERE n.nspname = t.table_schema AND c.relname = t.table_name), 0)
                FROM information_schema.tables t
                WHERE ($1::text IS NULL OR t.table_schema = $1)
                  AND t.table_schema NOT LIKE 'pg\_%' AND t.table_schema <> 'information_schema'
                  AND (($2 AND t.table_type = 'BASE TABLE') OR ($3 AND t.table_type = 'VIEW'))
                  AND ($4::text IS NULL OR t.table_name ILIKE '%' || $4 || '%')
                ORDER BY t.table_schema, t.table_name
                LIMIT {query.Limit.ToString(CultureInfo.InvariantCulture)} OFFSET {query.Offset.ToString(CultureInfo.InvariantCulture)};
                """;
            AddParameter(command, (object?)scope.Schema ?? DBNull.Value);
            AddParameter(command, query.IncludeTables);
            AddParameter(command, query.IncludeViews);
            AddParameter(command, (object?)query.NameLike ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new ObjectInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Type = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase) ? ObjectType.View : ObjectType.Table,
                    ApproxRows = Math.Max(0, reader.GetInt64(3)),
                });
            }
        }

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandTimeout = 0;
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM information_schema.tables t
                WHERE ($1::text IS NULL OR t.table_schema = $1)
                  AND t.table_schema NOT LIKE 'pg\_%' AND t.table_schema <> 'information_schema'
                  AND (($2 AND t.table_type = 'BASE TABLE') OR ($3 AND t.table_type = 'VIEW'))
                  AND ($4::text IS NULL OR t.table_name ILIKE '%' || $4 || '%');
                """;
            AddParameter(countCommand, (object?)scope.Schema ?? DBNull.Value);
            AddParameter(countCommand, query.IncludeTables);
            AddParameter(countCommand, query.IncludeViews);
            AddParameter(countCommand, (object?)query.NameLike ?? DBNull.Value);
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
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT LIKE 'pg\_%' AND table_schema <> 'information_schema'
              AND table_name ILIKE '%' || $1 || '%'
            ORDER BY table_schema, table_name
            LIMIT 100;
            """;
        AddParameter(command, term);

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

        var schema = string.IsNullOrEmpty(name.Schema) ? "public" : name.Schema;

        string? tableType = null;
        await using (var typeCommand = connection.CreateCommand())
        {
            typeCommand.CommandTimeout = 0;
            typeCommand.CommandText = "SELECT table_type FROM information_schema.tables WHERE table_schema = $1 AND table_name = $2;";
            AddParameter(typeCommand, schema);
            AddParameter(typeCommand, name.Name);
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
                SELECT c.column_name, c.ordinal_position, c.udt_name,
                       c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision,
                       c.is_nullable, c.collation_name, c.column_default, c.is_identity,
                       EXISTS (
                           SELECT 1
                           FROM information_schema.table_constraints tc
                           JOIN information_schema.key_column_usage k
                             ON k.constraint_name = tc.constraint_name AND k.constraint_schema = tc.constraint_schema
                           WHERE tc.constraint_type = 'PRIMARY KEY'
                             AND tc.table_schema = c.table_schema AND tc.table_name = c.table_name
                             AND k.column_name = c.column_name
                       ) AS is_pk
                FROM information_schema.columns c
                WHERE c.table_schema = $1 AND c.table_name = $2
                ORDER BY c.ordinal_position;
                """;
            AddParameter(command, schema);
            AddParameter(command, name.Name);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var udtName = reader.GetString(2);
                var charLength = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                var precision = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                var scale = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
                var datetimePrecision = reader.IsDBNull(6) ? (int?)null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
                var defaultExpression = reader.IsDBNull(9) ? null : reader.GetString(9);
                var isIdentity = string.Equals(reader.GetString(10), "YES", StringComparison.OrdinalIgnoreCase)
                    || (defaultExpression?.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase) ?? false);

                columns.Add(new CatalogColumn
                {
                    Name = reader.GetString(0),
                    Ordinal = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    NativeType = RenderNativeType(udtName, charLength, precision, scale, datetimePrecision),
                    IsNullable = string.Equals(reader.GetString(7), "YES", StringComparison.OrdinalIgnoreCase),
                    Collation = reader.IsDBNull(8) ? null : reader.GetString(8),
                    DefaultExpression = defaultExpression,
                    IsIdentity = isIdentity,
                    IsPrimaryKeyMember = reader.GetBoolean(11),
                });
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var isView = string.Equals(tableType, "VIEW", StringComparison.OrdinalIgnoreCase);
        var (definition, availability) = isView
            ? await ReadViewDefinitionAsync(connection, schema, name.Name, ct).ConfigureAwait(false)
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
    /// Reads a view's SQL with <c>pg_get_viewdef</c>, pretty-printed.
    /// <para>PostgreSQL has no separate view-definition privilege: a login that can see the relation can read its
    /// body, so the denied state does not arise here. What PostgreSQL returns is its own normalized rendering of
    /// the view rather than the text as originally typed, which is the canonical form and what psql's \d+ shows.</para>
    /// </summary>
    private static async Task<(string? Definition, DefinitionAvailability Availability)> ReadViewDefinitionAsync(
        DbConnection connection, string schema, string viewName, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT pg_get_viewdef(c.oid, true)
            FROM pg_class AS c
            JOIN pg_namespace AS n ON n.oid = c.relnamespace
            WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind IN ('v', 'm');
            """;
        AddParameter(command, schema);
        AddParameter(command, viewName);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var definition = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(definition)
            ? (null, DefinitionAvailability.Unavailable)
            : (definition, DefinitionAvailability.Available);
    }

    // The mapper consumes udt_name plus the relevant modifier: varchar(50), numeric(10,2), timestamptz(6).
    private static string RenderNativeType(string udtName, int? charLength, int? precision, int? scale, int? datetimePrecision)
    {
        if (charLength is { } length && udtName is "varchar" or "bpchar")
        {
            return $"{udtName}({length.ToString(CultureInfo.InvariantCulture)})";
        }

        if (udtName is "numeric" or "decimal" && precision is { } p)
        {
            return $"{udtName}({p.ToString(CultureInfo.InvariantCulture)},{(scale ?? 0).ToString(CultureInfo.InvariantCulture)})";
        }

        if (udtName is "timestamp" or "timestamptz" or "time" or "timetz" && datetimePrecision is { } dtp)
        {
            return $"{udtName}({dtp.ToString(CultureInfo.InvariantCulture)})";
        }

        return udtName;
    }

    private static void AddParameter(DbCommand command, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
