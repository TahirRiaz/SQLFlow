using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.SchemaRegistration;

namespace SqlFlow.SqlServer.Catalog;

/// <summary>What a harvest reads from one database.</summary>
public sealed record ObjectHarvestRequest
{
    /// <summary>True reads every object kind the lineage graph tracks (tables, views, procedures, functions,
    /// triggers) and every module body; false reads tables and views only, with only the views' bodies.</summary>
    public required bool IncludeProgrammability { get; init; }

    /// <summary>The database to switch to and the schemas to read.</summary>
    public SchemaRegistrationScope Scope { get; init; } = new();
}

/// <summary>One harvested object: its identity, kind, columns, primary key, and (for a table) its reconstructed
/// <c>CREATE TABLE</c> script.</summary>
public sealed record HarvestedObject
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required LineageNodeKind Kind { get; init; }

    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = [];

    /// <summary>The reconstructed <c>CREATE TABLE</c>; null for anything but a base table.</summary>
    public string? TableScript { get; init; }
}

/// <summary>One module row (<c>sys.sql_modules</c>); <see cref="Definition"/> is null for an encrypted module.</summary>
public sealed record HarvestedModule
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public string? Definition { get; init; }
}

/// <summary>Everything one harvest read, in catalog order (schema, then name).</summary>
public sealed record ObjectHarvest
{
    public required string Database { get; init; }

    public required IReadOnlyList<HarvestedObject> Objects { get; init; }

    public required IReadOnlyList<HarvestedModule> Modules { get; init; }
}

/// <summary>
/// The one reader of a SQL Server database's object metadata over an open connection: the object inventory, every
/// column, the primary keys, a reconstructed <c>CREATE TABLE</c> per base table (SQL Server keeps no table DDL
/// text), and the stored module bodies. The derived lineage tier and the schema registration flow both read
/// through here, so a table is described identically whichever of them registered it. Set-based reads only: one
/// query per kind of fact for the whole database, never one per object.
/// </summary>
public static class SqlServerObjectHarvester
{
    private const string AllObjectTypes = "'U','V','P','FN','IF','TF','TR'";
    private const string AllColumnTypes = "'U','V','IF','TF'";
    private const string SimpleObjectTypes = "'U','V'";

    public static async Task<ObjectHarvest> HarvestAsync(
        SqlConnection connection, ObjectHarvestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Scope.Database is { } requested)
        {
            await connection.ChangeDatabaseAsync(requested, ct).ConfigureAwait(false);
        }

        var database = await ScalarAsync(connection, "SELECT DB_NAME();", ct).ConfigureAwait(false) as string
            ?? throw new InvalidOperationException("the connection has no current database");
        if (request.Scope.Database is { } expected && !string.Equals(expected, database, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"switching to database '{expected}' left the connection in '{database}'");
        }

        var objectTypes = request.IncludeProgrammability ? AllObjectTypes : SimpleObjectTypes;
        var columnTypes = request.IncludeProgrammability ? AllColumnTypes : SimpleObjectTypes;

        var columns = await ColumnsAsync(connection, columnTypes, request.Scope, ct).ConfigureAwait(false);
        var tables = await TableShapesAsync(connection, request.Scope, ct).ConfigureAwait(false);
        var objects = await InventoryAsync(connection, objectTypes, request.Scope, columns, tables, ct).ConfigureAwait(false);
        var modules = await ModulesAsync(connection, request.IncludeProgrammability, request.Scope, ct).ConfigureAwait(false);

        return new ObjectHarvest { Database = database, Objects = objects, Modules = modules };
    }

    private static async Task<List<HarvestedObject>> InventoryAsync(
        SqlConnection connection, string types, SchemaRegistrationScope scope,
        Dictionary<string, List<LineageColumn>> columns, Dictionary<string, TableShape> tables, CancellationToken ct)
    {
        await using var command = new SqlCommand { Connection = connection, CommandTimeout = 0 };
        command.CommandText = $"""
            SELECT s.name, o.name, o.type
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ({types}) AND o.is_ms_shipped = 0{ScopePredicate(command, scope)}
            ORDER BY s.name, o.name;
            """;

        var result = new List<HarvestedObject>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var key = schema + "|" + name;
            var kind = reader.GetString(2).TrimEnd() switch
            {
                "U" => LineageNodeKind.Table,
                "V" => LineageNodeKind.View,
                "P" => LineageNodeKind.Procedure,
                "FN" or "IF" or "TF" => LineageNodeKind.Function,
                "TR" => LineageNodeKind.Trigger,
                _ => LineageNodeKind.Unknown,
            };
            tables.TryGetValue(key, out var shape);
            result.Add(new HarvestedObject
            {
                Schema = schema,
                Name = name,
                Kind = kind,
                Columns = columns.TryGetValue(key, out var objectColumns) ? objectColumns : [],
                PrimaryKeyColumns = shape?.PrimaryKeyColumns ?? [],
                TableScript = kind == LineageNodeKind.Table ? shape?.Render() : null,
            });
        }

        return result;
    }

    /// <summary>The columns of every object of <paramref name="types"/>, keyed by <c>schema|name</c>
    /// (case-insensitive, matching SQL Server's default object-name collation).</summary>
    private static async Task<Dictionary<string, List<LineageColumn>>> ColumnsAsync(
        SqlConnection connection, string types, SchemaRegistrationScope scope, CancellationToken ct)
    {
        await using var command = new SqlCommand { Connection = connection, CommandTimeout = 0 };
        command.CommandText = $"""
            SELECT s.name, o.name, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ({types}) AND o.is_ms_shipped = 0{ScopePredicate(command, scope)}
            ORDER BY s.name, o.name, c.column_id;
            """;

        var map = new Dictionary<string, List<LineageColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0) + "|" + reader.GetString(1);
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(new LineageColumn
            {
                Ordinal = reader.GetInt32(2),
                Name = reader.GetString(3),
                DataType = RenderType(reader.GetString(4), reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7)),
                Nullable = reader.GetBoolean(8),
            });
        }

        return map;
    }

    /// <summary>
    /// Every base table's rendered column lines (with identity, computed expressions, nullability) and primary key,
    /// keyed by <c>schema|name</c>: the material for the reconstructed <c>CREATE TABLE</c> and the key columns.
    /// </summary>
    private static async Task<Dictionary<string, TableShape>> TableShapesAsync(
        SqlConnection connection, SchemaRegistrationScope scope, CancellationToken ct)
    {
        var tables = new Dictionary<string, TableShape>(StringComparer.OrdinalIgnoreCase);
        TableShape Table(string schema, string name)
        {
            var key = schema + "|" + name;
            if (!tables.TryGetValue(key, out var table))
            {
                tables[key] = table = new TableShape { Schema = schema, Name = name };
            }

            return table;
        }

        await using (var command = new SqlCommand { Connection = connection, CommandTimeout = 0 })
        {
            command.CommandText = $"""
                SELECT s.name, o.name, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                       CONVERT(bigint, ISNULL(ic.seed_value, 1)), CONVERT(bigint, ISNULL(ic.increment_value, 1)),
                       c.is_computed, cc.definition
                FROM sys.columns c
                JOIN sys.objects o ON o.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
                WHERE o.type = 'U' AND o.is_ms_shipped = 0{ScopePredicate(command, scope)}
                ORDER BY s.name, o.name, c.column_id;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = Table(reader.GetString(0), reader.GetString(1));
                var columnName = reader.GetString(2);
                if (reader.GetBoolean(11))
                {
                    // A computed column: [name] AS (expression); it carries no type or nullability of its own.
                    var definition = reader.IsDBNull(12) ? "NULL" : reader.GetString(12);
                    table.Columns.Add($"    [{columnName}] AS {definition}");
                    continue;
                }

                var type = RenderType(reader.GetString(3), reader.GetInt16(4), reader.GetByte(5), reader.GetByte(6));
                var identity = reader.GetBoolean(8)
                    ? string.Create(CultureInfo.InvariantCulture, $" IDENTITY({reader.GetInt64(9)},{reader.GetInt64(10)})")
                    : string.Empty;
                var nullability = reader.GetBoolean(7) ? "NULL" : "NOT NULL";
                table.Columns.Add($"    [{columnName}] {type}{identity} {nullability}");
            }
        }

        await using (var command = new SqlCommand { Connection = connection, CommandTimeout = 0 })
        {
            command.CommandText = $"""
                SELECT s.name, o.name, kc.name, i.type_desc, col.name
                FROM sys.indexes i
                JOIN sys.objects o ON o.object_id = i.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
                WHERE i.is_primary_key = 1 AND o.type = 'U' AND o.is_ms_shipped = 0{ScopePredicate(command, scope)}
                ORDER BY s.name, o.name, ic.key_ordinal;
                """;

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = Table(reader.GetString(0), reader.GetString(1));
                table.PrimaryKeyName = reader.GetString(2);
                table.PrimaryKeyClustered = reader.GetString(3);   // CLUSTERED / NONCLUSTERED
                table.PrimaryKeyColumns.Add(reader.GetString(4));
            }
        }

        return tables;
    }

    /// <summary>The stored module bodies in scope: every non-shipped module when programmability is harvested,
    /// otherwise only views'.</summary>
    private static async Task<List<HarvestedModule>> ModulesAsync(
        SqlConnection connection, bool includeProgrammability, SchemaRegistrationScope scope, CancellationToken ct)
    {
        await using var command = new SqlCommand { Connection = connection, CommandTimeout = 0 };
        var kindFilter = includeProgrammability ? string.Empty : " AND o.type = 'V'";
        command.CommandText = $"""
            SELECT s.name, o.name, m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0{kindFilter}{ScopePredicate(command, scope)}
            ORDER BY s.name, o.name;
            """;

        var modules = new List<HarvestedModule>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            modules.Add(new HarvestedModule
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                Definition = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }

        return modules;
    }

    /// <summary>
    /// The schema filter as a SQL predicate over <c>s.name</c>, every name bound as a parameter on
    /// <paramref name="command"/>. Compared upper-cased on both sides so the filter is case-insensitive under a
    /// case-sensitive database collation too, matching how the flow document compares schema names.
    /// </summary>
    private static string ScopePredicate(SqlCommand command, SchemaRegistrationScope scope)
    {
        var sql = new StringBuilder();
        Append("IN", "si", scope.IncludeSchemas);
        Append("NOT IN", "sx", scope.ExcludeSchemas);
        return sql.ToString();

        void Append(string op, string prefix, IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            var parameters = new List<string>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                var parameter = string.Create(CultureInfo.InvariantCulture, $"@{prefix}{i}");
                command.Parameters.Add(new SqlParameter(parameter, System.Data.SqlDbType.NVarChar, 128)
                {
                    Value = names[i].ToUpperInvariant(),
                });
                parameters.Add(parameter);
            }

            sql.Append(CultureInfo.InvariantCulture, $" AND UPPER(s.name) {op} ({string.Join(", ", parameters)})");
        }
    }

    /// <summary>Renders a SQL Server type with its length/precision the way it reads in DDL:
    /// <c>nvarchar(100)</c>, <c>nvarchar(max)</c>, <c>decimal(18,2)</c>, <c>datetime2(7)</c>.</summary>
    private static string RenderType(string typeName, short maxLength, byte precision, byte scale)
        => typeName switch
        {
            "nvarchar" or "nchar" => $"{typeName}({Length(maxLength == -1 ? -1 : maxLength / 2)})",
            "varchar" or "char" or "varbinary" or "binary" => $"{typeName}({Length(maxLength)})",
            "decimal" or "numeric" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision},{scale})"),
            "datetime2" or "datetimeoffset" or "time" => string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})"),
            _ => typeName,
        };

    private static string Length(int maxLength) => maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One base table's rendered column lines and primary key, accumulated while the schema is read.</summary>
    private sealed class TableShape
    {
        public required string Schema { get; init; }

        public required string Name { get; init; }

        public List<string> Columns { get; } = [];

        public string? PrimaryKeyName { get; set; }

        public string PrimaryKeyClustered { get; set; } = "CLUSTERED";

        public List<string> PrimaryKeyColumns { get; } = [];

        public string Render()
        {
            var lines = new List<string>(Columns);
            if (PrimaryKeyColumns.Count > 0)
            {
                var keyColumns = string.Join(", ", PrimaryKeyColumns.Select(c => $"[{c}] ASC"));
                lines.Add($"    CONSTRAINT [{PrimaryKeyName}] PRIMARY KEY {PrimaryKeyClustered} ({keyColumns})");
            }

            return $"CREATE TABLE [{Schema}].[{Name}] (\n{string.Join(",\n", lines)}\n);";
        }
    }
}
