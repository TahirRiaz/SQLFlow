using System.Data.Common;
using System.Globalization;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.Catalog;

/// <summary>
/// Reads the SQL Server catalog over a supplied open connection: lists databases, schemas, and objects,
/// searches, and fully introspects a table or view (columns and indexes). Least-privilege by construction
/// (HAS_DBACCESS, is_ms_shipped = 0); user search values are always parameters with an explicit LIKE ESCAPE,
/// and the only interpolated value is the integer page size. Constraint introspection is a later addition.
/// </summary>
public sealed class SqlServerCatalogReader : IProviderCatalogReader
{
    /// <summary>
    /// Every command here waits on the server rather than a client clock. These catalog scans run on the hot
    /// ingestion path (schema introspection before each load) and take metadata locks that queue behind any
    /// concurrent DDL transaction in the same database, so under a wide batch fire they routinely wait longer
    /// than ADO.NET's 30 second default. That default would abort the whole flow with a bare "Execution Timeout
    /// Expired" instead of letting the introspection complete.
    /// </summary>
    private const int NoClientTimeout = 0;

    public bool CanHandle(DataSourceKind kind) => kind is DataSourceKind.MSSQL or DataSourceKind.AZDB;

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection connection, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT d.name AS DatabaseName, d.collation_name AS Collation, d.state_desc AS State
            FROM sys.databases AS d
            WHERE d.state = 0
              AND HAS_DBACCESS(d.name) = 1
              AND (@includeSystem = 1 OR d.database_id > 4)
              AND (@nameLike IS NULL OR d.name LIKE @nameLike ESCAPE '\')
            ORDER BY d.name
            OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY;
            """;
        AddParam(command, "@includeSystem", query.IncludeSystem ? 1 : 0);
        AddParam(command, "@nameLike", (object?)Contains(query.NameLike) ?? DBNull.Value);
        AddParam(command, "@off", query.Offset);
        AddParam(command, "@lim", ClampLimit(query.Limit));

        var result = new List<DatabaseInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DatabaseInfo { Name = Str(reader, "DatabaseName")!, Collation = Str(reader, "Collation"), State = Str(reader, "State") });
        }

        return result;
    }

    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(DbConnection connection, string? database, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);
        UseDatabase(connection, database);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT s.name AS SchemaName, p.name AS Owner
            FROM sys.schemas AS s
            JOIN sys.database_principals AS p ON p.principal_id = s.principal_id
            WHERE (@includeSystem = 1 OR s.name NOT IN
                    ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin','db_securityadmin',
                     'db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter'))
              AND (@nameLike IS NULL OR s.name LIKE @nameLike ESCAPE '\')
            ORDER BY s.name;
            """;
        AddParam(command, "@includeSystem", query.IncludeSystem ? 1 : 0);
        AddParam(command, "@nameLike", (object?)Contains(query.NameLike) ?? DBNull.Value);

        var result = new List<SchemaInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new SchemaInfo { Name = Str(reader, "SchemaName")!, Owner = Str(reader, "Owner") });
        }

        return result;
    }

    public async Task<CatalogPage<ObjectInfo>> ListObjectsAsync(DbConnection connection, ObjectScope scope, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        UseDatabase(connection, scope.Database);

        var limit = ClampLimit(query.Limit);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT o.[name] AS ObjectName,
                   SCHEMA_NAME(o.schema_id) AS SchemaName,
                   o.[type] AS ObjectTypeCode,
                   CAST(ISNULL(ps.row_count, 0) AS bigint) AS ApproxRows,
                   COUNT(*) OVER () AS Total
            FROM sys.objects AS o
            OUTER APPLY (
                SELECT SUM(p.[rows]) AS row_count
                FROM sys.partitions AS p
                WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)
            ) AS ps
            WHERE o.[type] IN ('U', 'V')
              AND ((@includeTables = 1 AND o.[type] = 'U') OR (@includeViews = 1 AND o.[type] = 'V'))
              AND (@schema IS NULL OR SCHEMA_NAME(o.schema_id) = @schema)
              AND (@nameLike IS NULL OR o.[name] LIKE @nameLike ESCAPE '\')
              AND o.is_ms_shipped = 0
            ORDER BY SCHEMA_NAME(o.schema_id), o.[name]
            OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY;
            """;
        AddParam(command, "@includeTables", query.IncludeTables ? 1 : 0);
        AddParam(command, "@includeViews", query.IncludeViews ? 1 : 0);
        AddParam(command, "@schema", (object?)scope.Schema ?? DBNull.Value);
        AddParam(command, "@nameLike", (object?)Contains(query.NameLike) ?? DBNull.Value);
        AddParam(command, "@off", query.Offset);
        AddParam(command, "@lim", limit);

        var items = new List<ObjectInfo>();
        long total = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            total = Long(reader, "Total");
            items.Add(new ObjectInfo
            {
                Schema = Str(reader, "SchemaName")!,
                Name = Str(reader, "ObjectName")!,
                Type = TypeOf(Str(reader, "ObjectTypeCode")),
                ApproxRows = Long(reader, "ApproxRows"),
            });
        }

        return new CatalogPage<ObjectInfo> { Items = items, Offset = query.Offset, Limit = limit, Total = total };
    }

    public async Task<IReadOnlyList<ObjectMatch>> SearchObjectsAsync(DbConnection connection, string? database, string term, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        UseDatabase(connection, database);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT TOP (@lim)
                   SCHEMA_NAME(o.schema_id) AS SchemaName,
                   o.[name] AS ObjectName,
                   o.[type] AS ObjectTypeCode,
                   CASE WHEN o.[name] = @term THEN 0
                        WHEN o.[name] LIKE @prefix ESCAPE '\' THEN 1
                        ELSE 2 END AS Rank
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0
              AND o.[type] IN ('U', 'V')
              AND o.[name] LIKE @contains ESCAPE '\'
            ORDER BY Rank, SchemaName, ObjectName;
            """;
        AddParam(command, "@lim", 200);
        AddParam(command, "@term", term);
        AddParam(command, "@prefix", EscapeLike(term) + "%");
        AddParam(command, "@contains", "%" + EscapeLike(term) + "%");

        var result = new List<ObjectMatch>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ObjectMatch
            {
                Schema = Str(reader, "SchemaName")!,
                Name = Str(reader, "ObjectName")!,
                Type = TypeOf(Str(reader, "ObjectTypeCode")),
                Rank = Int(reader, "Rank"),
            });
        }

        return result;
    }

    public async Task<CatalogObject?> IntrospectObjectAsync(DbConnection connection, ThreePartName name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(name);
        UseDatabase(connection, name.Database);

        int objectId;
        ObjectType type;
        bool temporal;
        bool hasPeriod;
        string? historySchema;
        string? historyTable;

        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandTimeout = NoClientTimeout;
            // The SYSTEM_TIME period is read from sys.periods rather than inferred from temporal_type: the
            // period outlives SET (SYSTEM_VERSIONING = OFF), so a table can carry one while not being
            // temporal, and that is precisely the state the temporal planner must be able to resume from.
            lookup.CommandText = """
                SELECT o.object_id AS ObjectId, o.[type] AS ObjectTypeCode, ISNULL(tb.temporal_type, 0) AS TemporalType,
                       CASE WHEN p.object_id IS NULL THEN 0 ELSE 1 END AS HasPeriod,
                       SCHEMA_NAME(h.schema_id) AS HistorySchema, h.[name] AS HistoryTable
                FROM sys.objects AS o
                LEFT JOIN sys.tables AS tb ON tb.object_id = o.object_id
                LEFT JOIN sys.tables AS h ON h.object_id = tb.history_table_id
                LEFT JOIN sys.periods AS p ON p.object_id = o.object_id AND p.period_type = 1
                WHERE o.object_id = OBJECT_ID(@q) AND o.[type] IN ('U', 'V');
                """;
            AddParam(lookup, "@q", name.SchemaQualified);

            await using var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            objectId = Int(reader, "ObjectId");
            type = TypeOf(Str(reader, "ObjectTypeCode"));
            temporal = Int(reader, "TemporalType") == 2;
            hasPeriod = Int(reader, "HasPeriod") == 1;
            historySchema = Str(reader, "HistorySchema");
            historyTable = Str(reader, "HistoryTable");
        }

        var columns = await ReadColumnsAsync(connection, objectId, ct).ConfigureAwait(false);
        var indexes = await ReadIndexesAsync(connection, objectId, ct).ConfigureAwait(false);
        var retentionDays = temporal
            ? await ReadHistoryRetentionDaysAsync(connection, objectId, ct).ConfigureAwait(false)
            : null;
        var (definition, availability) = type == ObjectType.View
            ? await ReadViewDefinitionAsync(connection, name, ct).ConfigureAwait(false)
            : (null, DefinitionAvailability.NotApplicable);

        return new CatalogObject
        {
            Name = name,
            Type = type,
            Columns = columns,
            Indexes = indexes,
            Definition = definition,
            DefinitionAvailability = availability,
            IsTemporal = temporal,
            HasSystemTimePeriod = hasPeriod,
            HistorySchema = historySchema,
            HistoryTable = historyTable,
            HistoryRetentionDays = retentionDays,
        };
    }

    /// <summary>
    /// Reads a view's own SQL from <c>sys.sql_modules</c>.
    /// <para>The privilege is checked first rather than inferred from a null definition, because SQL Server does
    /// not raise on an unprivileged read: it silently returns no row, which is the same answer it gives for an
    /// encrypted module. A reporting login commonly holds SELECT on a view without VIEW DEFINITION, so telling
    /// the operator "your login may not read this view's source" instead of "this view has no source" is the
    /// difference between asking for a grant and chasing a phantom.</para>
    /// </summary>
    private static async Task<(string? Definition, DefinitionAvailability Availability)> ReadViewDefinitionAsync(
        DbConnection connection, ThreePartName name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT HAS_PERMS_BY_NAME(@q, 'OBJECT', 'VIEW DEFINITION') AS Permitted,
                   (SELECT m.definition FROM sys.sql_modules AS m WHERE m.object_id = OBJECT_ID(@q)) AS Definition;
            """;
        AddParam(command, "@q", name.SchemaQualified);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (null, DefinitionAvailability.Unavailable);
        }

        // HAS_PERMS_BY_NAME itself returns NULL for a name it cannot resolve; treat that as denied rather than
        // granted, so an ambiguous answer never reads as "this view genuinely has no source".
        var permittedOrdinal = reader.GetOrdinal("Permitted");
        var permitted = !reader.IsDBNull(permittedOrdinal) && reader.GetInt32(permittedOrdinal) == 1;
        if (!permitted)
        {
            return (null, DefinitionAvailability.PermissionDenied);
        }

        var definition = Str(reader, "Definition");
        return string.IsNullOrWhiteSpace(definition)
            ? (null, DefinitionAvailability.Unavailable)
            : (definition, DefinitionAvailability.Available);
    }

    /// <summary>
    /// Reads HISTORY_RETENTION_PERIOD for a temporal table, normalized to days (null = INFINITE, which SQL
    /// Server stores as -1). The <c>sys.tables</c> retention columns do not exist on every supported engine,
    /// so the projection is built through sp_executesql behind a COL_LENGTH guard: on an engine without them
    /// the query returns NULL instead of failing to compile the whole introspection.
    /// </summary>
    private static async Task<int?> ReadHistoryRetentionDaysAsync(DbConnection connection, int objectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            DECLARE @sql nvarchar(max) =
                CASE WHEN COL_LENGTH('sys.tables', 'history_retention_period') IS NULL
                     THEN N'SELECT CONVERT(int, NULL) AS Period, CONVERT(nvarchar(20), NULL) AS Unit;'
                     ELSE N'SELECT history_retention_period AS Period, history_retention_period_unit_desc AS Unit
                            FROM sys.tables WHERE object_id = @id;' END;
            EXEC sp_executesql @sql, N'@id int', @id = @objectId;
            """;
        AddParam(command, "@objectId", objectId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var period = IntN(reader, "Period");
        // -1 is INFINITE; 0 and null mean the engine does not express a finite retention here either.
        if (period is null or <= 0)
        {
            return null;
        }

        return Str(reader, "Unit")?.ToUpperInvariant() switch
        {
            "DAY" => period,
            "WEEK" => period * 7,
            "MONTH" => period * 30,
            "YEAR" => period * 365,
            _ => null,
        };
    }

    private static async Task<IReadOnlyList<CatalogColumn>> ReadColumnsAsync(DbConnection connection, int objectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT c.column_id AS Ordinal, c.[name] AS ColumnName, t.[name] AS BaseTypeName,
                   c.max_length AS MaxLengthBytes, c.[precision] AS Prec, c.scale AS Scale,
                   c.is_nullable AS IsNullable, c.collation_name AS Collation, c.is_identity AS IsIdentity,
                   ic.seed_value AS IdentitySeed, ic.increment_value AS IdentityIncrement,
                   cc.[definition] AS ComputedDefinition, cc.is_persisted AS ComputedPersisted,
                   dc.[definition] AS DefaultDefinition,
                   c.generated_always_type AS GeneratedAlwaysType, c.is_hidden AS IsHidden,
                   CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS IsPkMember
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            OUTER APPLY (
                SELECT TOP 1 ic2.column_id
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic2 ON ic2.object_id = i.object_id AND ic2.index_id = i.index_id
                WHERE i.is_primary_key = 1 AND ic2.object_id = c.object_id AND ic2.column_id = c.column_id
            ) AS pk
            WHERE c.object_id = @objectId
            ORDER BY c.column_id;
            """;
        AddParam(command, "@objectId", objectId);

        var columns = new List<CatalogColumn>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var baseType = Str(reader, "BaseTypeName")!;
            columns.Add(new CatalogColumn
            {
                Ordinal = Int(reader, "Ordinal"),
                Name = Str(reader, "ColumnName")!,
                NativeType = RenderNativeType(baseType, Int(reader, "MaxLengthBytes"), Int(reader, "Prec"), Int(reader, "Scale")),
                IsNullable = Bool(reader, "IsNullable"),
                Collation = Str(reader, "Collation"),
                IsIdentity = Bool(reader, "IsIdentity"),
                IdentitySeed = LongN(reader, "IdentitySeed"),
                IdentityIncrement = LongN(reader, "IdentityIncrement"),
                ComputedExpression = Str(reader, "ComputedDefinition"),
                ComputedPersisted = Bool(reader, "ComputedPersisted"),
                DefaultExpression = Str(reader, "DefaultDefinition"),
                IsPrimaryKeyMember = Int(reader, "IsPkMember") == 1,
                // 1 = AS_ROW_START, 2 = AS_ROW_END; every other value (including the ledger ones) is an
                // ordinary column as far as the ingestion engine is concerned.
                GeneratedAlways = Int(reader, "GeneratedAlwaysType") switch
                {
                    1 => GeneratedAlwaysKind.RowStart,
                    2 => GeneratedAlwaysKind.RowEnd,
                    _ => GeneratedAlwaysKind.None,
                },
                IsHidden = Bool(reader, "IsHidden"),
            });
        }

        return columns;
    }

    private static async Task<IReadOnlyList<CatalogIndex>> ReadIndexesAsync(DbConnection connection, int objectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = NoClientTimeout;
        command.CommandText = """
            SELECT i.[name] AS IndexName, i.is_primary_key AS IsPk, i.is_unique AS IsUnique,
                   CASE WHEN i.[type] = 1 THEN 1 ELSE 0 END AS IsClustered,
                   CASE WHEN i.[type] IN (5, 6) THEN 1 ELSE 0 END AS IsColumnStore,
                   c.[name] AS ColumnName
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @objectId AND i.[type] <> 0 AND i.[name] IS NOT NULL AND ic.is_included_column = 0
            ORDER BY i.index_id, ic.key_ordinal, ic.index_column_id;
            """;
        AddParam(command, "@objectId", objectId);

        var byName = new Dictionary<string, (CatalogIndex Meta, List<string> Columns)>(StringComparer.Ordinal);
        var order = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var indexName = Str(reader, "IndexName")!;
            if (!byName.TryGetValue(indexName, out var entry))
            {
                entry = (
                    new CatalogIndex
                    {
                        Name = indexName,
                        IsPrimaryKey = Bool(reader, "IsPk"),
                        IsUnique = Bool(reader, "IsUnique"),
                        IsClustered = Int(reader, "IsClustered") == 1,
                        IsColumnStore = Int(reader, "IsColumnStore") == 1,
                        KeyColumns = [],
                    },
                    new List<string>());
                byName[indexName] = entry;
                order.Add(indexName);
            }

            entry.Columns.Add(Str(reader, "ColumnName")!);
        }

        return order.Select(n => byName[n].Meta with { KeyColumns = byName[n].Columns }).ToList();
    }

    private static void UseDatabase(DbConnection connection, string? database)
    {
        if (!string.IsNullOrEmpty(database) && !string.Equals(connection.Database, database, StringComparison.OrdinalIgnoreCase))
        {
            connection.ChangeDatabase(database);
        }
    }

    private static ObjectType TypeOf(string? typeCode)
        => string.Equals(typeCode?.Trim(), "V", StringComparison.OrdinalIgnoreCase) ? ObjectType.View : ObjectType.Table;

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, 2000);

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    private static string? Contains(string? nameLike)
        => string.IsNullOrEmpty(nameLike) ? null : "%" + EscapeLike(nameLike) + "%";

    private static string RenderNativeType(string baseType, int maxLengthBytes, int precision, int scale)
    {
        var t = baseType.ToLowerInvariant();
        return t switch
        {
            "nvarchar" or "nchar" => maxLengthBytes == -1 ? $"{t}(max)" : $"{t}({(maxLengthBytes / 2).ToString(CultureInfo.InvariantCulture)})",
            "varchar" or "char" or "varbinary" or "binary" => maxLengthBytes == -1 ? $"{t}(max)" : $"{t}({maxLengthBytes.ToString(CultureInfo.InvariantCulture)})",
            "decimal" or "numeric" => $"{t}({precision.ToString(CultureInfo.InvariantCulture)}, {scale.ToString(CultureInfo.InvariantCulture)})",
            "datetime2" or "time" or "datetimeoffset" => $"{t}({scale.ToString(CultureInfo.InvariantCulture)})",
            _ => t,
        };
    }

    private static void AddParam(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string? Str(DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : reader.GetString(i);
    }

    private static int Int(DbDataReader reader, string column)
        => Convert.ToInt32(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    private static long Long(DbDataReader reader, string column)
        => Convert.ToInt64(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    private static int? IntN(DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);
    }

    private static long? LongN(DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
    }

    private static bool Bool(DbDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
    }
}
