using System.Data.Common;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.Providers.Oracle;

/// <summary>
/// Reads the Oracle catalog over a supplied open connection through the <c>ALL_*</c> data-dictionary views.
/// An Oracle connection targets one pluggable database (a service), so the database listing returns that
/// container only. Oracle has no separate schema level below the user: a schema IS a user, so schemas come
/// from <c>ALL_USERS</c> and an object's schema part addresses its owner. Oracle-maintained schemas (SYS,
/// SYSTEM's dictionary users, and the like) are hidden unless the caller asks for system objects. The column
/// NativeType is rendered from <c>DATA_TYPE</c> plus its modifiers (<c>NUMBER(10,2)</c>, <c>VARCHAR2(50)</c>,
/// <c>TIMESTAMP(6) WITH TIME ZONE</c>), which the Oracle type mapper translates. Bind values are always
/// parameters, and every command binds by name so a reused placeholder resolves to one parameter.
/// </summary>
public sealed class OracleCatalogReader : IProviderCatalogReader
{
    public bool CanHandle(DataSourceKind kind) => kind == DataSourceKind.Oracle;

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection connection, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = CreateCommand(connection);
        // The pluggable database (service) the connection is pinned to; Oracle has no cross-container queries.
        command.CommandText = "SELECT SYS_CONTEXT('USERENV', 'CON_NAME') FROM DUAL";
        var name = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? string.Empty;
        return [new DatabaseInfo { Name = name, State = "ONLINE" }];
    }

    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(DbConnection connection, string? database, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(query);

        await using var command = CreateCommand(connection);
        command.CommandText = """
            SELECT username
            FROM all_users
            WHERE (:includeSystem = 1 OR oracle_maintained = 'N')
            ORDER BY username
            """;
        AddParameter(command, "includeSystem", query.IncludeSystem ? 1 : 0);

        var schemas = new List<SchemaInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // In Oracle a schema and its owning user are the same principal.
            var name = reader.GetString(0);
            schemas.Add(new SchemaInfo { Name = name, Owner = name });
        }

        return schemas;
    }

    public async Task<CatalogPage<ObjectInfo>> ListObjectsAsync(DbConnection connection, ObjectScope scope, CatalogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var owner = scope.Schema ?? scope.Database;
        var items = new List<ObjectInfo>();
        long total;

        await using (var command = CreateCommand(connection))
        {
            command.CommandText = $"""
                SELECT owner, object_name, otype, num_rows
                FROM ({ObjectSource})
                WHERE (:owner IS NULL OR owner = :owner)
                  AND owner NOT IN (SELECT username FROM all_users WHERE oracle_maintained = 'Y')
                  AND ((:tables = 1 AND otype = 'TABLE') OR (:views = 1 AND otype = 'VIEW'))
                  AND (:namelike IS NULL OR UPPER(object_name) LIKE '%' || UPPER(:namelike) || '%')
                ORDER BY owner, object_name
                OFFSET {query.Offset.ToString(CultureInfo.InvariantCulture)} ROWS
                FETCH NEXT {query.Limit.ToString(CultureInfo.InvariantCulture)} ROWS ONLY
                """;
            AddObjectFilters(command, owner, query);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new ObjectInfo
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Type = string.Equals(reader.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase) ? ObjectType.View : ObjectType.Table,
                    ApproxRows = reader.IsDBNull(3) ? 0 : Math.Max(0, Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)),
                });
            }
        }

        await using (var countCommand = CreateCommand(connection))
        {
            countCommand.CommandText = $"""
                SELECT COUNT(*)
                FROM ({ObjectSource})
                WHERE (:owner IS NULL OR owner = :owner)
                  AND owner NOT IN (SELECT username FROM all_users WHERE oracle_maintained = 'Y')
                  AND ((:tables = 1 AND otype = 'TABLE') OR (:views = 1 AND otype = 'VIEW'))
                  AND (:namelike IS NULL OR UPPER(object_name) LIKE '%' || UPPER(:namelike) || '%')
                """;
            AddObjectFilters(countCommand, owner, query);
            total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        return new CatalogPage<ObjectInfo> { Items = items, Offset = query.Offset, Limit = query.Limit, Total = total };
    }

    public async Task<IReadOnlyList<ObjectMatch>> SearchObjectsAsync(DbConnection connection, string? database, string term, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);

        await using var command = CreateCommand(connection);
        command.CommandText = $"""
            SELECT owner, object_name, otype
            FROM ({ObjectSource})
            WHERE owner NOT IN (SELECT username FROM all_users WHERE oracle_maintained = 'Y')
              AND UPPER(object_name) LIKE '%' || UPPER(:term) || '%'
            ORDER BY owner, object_name
            FETCH FIRST 100 ROWS ONLY
            """;
        AddParameter(command, "term", term);

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

        // An empty schema part means the connection's current schema (its default owner).
        var owner = string.IsNullOrEmpty(name.Schema) ? null : name.Schema;

        string? objectType;
        await using (var typeCommand = CreateCommand(connection))
        {
            typeCommand.CommandText = """
                SELECT object_type FROM all_objects
                WHERE owner = COALESCE(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
                  AND object_name = :name AND object_type IN ('TABLE', 'VIEW')
                """;
            AddParameter(typeCommand, "owner", owner);
            AddParameter(typeCommand, "name", name.Name);
            objectType = await typeCommand.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (objectType is null)
        {
            return null;
        }

        var columns = new List<CatalogColumn>();
        await using (var command = CreateCommand(connection))
        {
            // DATA_DEFAULT is a LONG; fetch it whole so the default expression is captured, never truncated.
            command.InitialLONGFetchSize = -1;
            command.CommandText = """
                SELECT c.column_name, c.column_id, c.data_type, c.data_length, c.data_precision, c.data_scale,
                       c.char_length, c.nullable, c.collation, c.data_default,
                       CASE WHEN ic.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_identity,
                       CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_pk
                FROM all_tab_columns c
                LEFT JOIN all_tab_identity_cols ic
                       ON ic.owner = c.owner AND ic.table_name = c.table_name AND ic.column_name = c.column_name
                LEFT JOIN (
                    SELECT acc.owner, acc.table_name, acc.column_name
                    FROM all_constraints ac
                    JOIN all_cons_columns acc
                      ON acc.owner = ac.owner AND acc.constraint_name = ac.constraint_name
                    WHERE ac.constraint_type = 'P'
                ) pk ON pk.owner = c.owner AND pk.table_name = c.table_name AND pk.column_name = c.column_name
                WHERE c.owner = COALESCE(:owner, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')) AND c.table_name = :name
                ORDER BY c.column_id
                """;
            AddParameter(command, "owner", owner);
            AddParameter(command, "name", name.Name);

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var dataType = reader.GetString(2);
                var dataLength = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                var precision = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
                var scale = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
                var charLength = reader.IsDBNull(6) ? (int?)null : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
                var defaultExpression = reader.IsDBNull(9) ? null : reader.GetString(9).Trim();

                columns.Add(new CatalogColumn
                {
                    Name = reader.GetString(0),
                    Ordinal = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    NativeType = RenderNativeType(dataType, dataLength, precision, scale, charLength),
                    IsNullable = string.Equals(reader.GetString(7), "Y", StringComparison.OrdinalIgnoreCase),
                    Collation = reader.IsDBNull(8) ? null : reader.GetString(8),
                    DefaultExpression = string.IsNullOrEmpty(defaultExpression) ? null : defaultExpression,
                    IsIdentity = Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture) == 1,
                    IsPrimaryKeyMember = Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture) == 1,
                });
            }
        }

        if (columns.Count == 0)
        {
            return null;
        }

        var isView = string.Equals(objectType, "VIEW", StringComparison.OrdinalIgnoreCase);
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
    /// Reads a view's SQL through <c>DBMS_METADATA.GET_DDL</c>.
    /// <para>Not from <c>all_views</c>: its TEXT column is a LONG, which the managed driver truncates to
    /// InitialLONGFetchSize, and its TEXT_VC alternative is VARCHAR2(4000). Both would hand back a silently
    /// shortened view, which is worse than none at all. GET_DDL returns a CLOB and so is complete at any length.</para>
    /// <para>Oracle signals a missing privilege by raising (ORA-31603 among others) rather than by returning
    /// nothing, so that is the one provider where the denied state is recognised from the error.</para>
    /// </summary>
    private static async Task<(string? Definition, DefinitionAvailability Availability)> ReadViewDefinitionAsync(
        DbConnection connection, ThreePartName name, CancellationToken ct)
    {
        await using var command = CreateCommand(connection);
        command.CommandText = "SELECT DBMS_METADATA.GET_DDL('VIEW', :objectName, :owner) FROM dual";
        AddParameter(command, "objectName", name.Name);
        AddParameter(command, "owner", name.Schema);

        try
        {
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var definition = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(definition)
                ? (null, DefinitionAvailability.Unavailable)
                : (definition, DefinitionAvailability.Available);
        }
        catch (OracleException ex) when (ex.Number is 31603 or 31604 or 1031)
        {
            // 31603/31604: the object is not visible to GET_DDL for this login. 1031: insufficient privileges.
            return (null, DefinitionAvailability.PermissionDenied);
        }
    }

    // Tables and views unified so listing, counting, and search share one filtered projection. NUM_ROWS is the
    // optimizer's row estimate (null until the table is analyzed); views have none.
    private const string ObjectSource = """
        SELECT owner, table_name AS object_name, 'TABLE' AS otype, num_rows FROM all_tables
        UNION ALL
        SELECT owner, view_name AS object_name, 'VIEW' AS otype, CAST(NULL AS NUMBER) AS num_rows FROM all_views
        """;

    // NUMBER carries optional precision/scale; VARCHAR2/CHAR family carry a character length; RAW a byte length;
    // TIMESTAMP/INTERVAL/DATE and the LOB/binary-float types already render their own modifiers in DATA_TYPE.
    private static string RenderNativeType(string dataType, int? dataLength, int? precision, int? scale, int? charLength)
    {
        var type = dataType.ToUpperInvariant();

        if (type == "NUMBER")
        {
            if (precision is { } p)
            {
                return $"NUMBER({p.ToString(CultureInfo.InvariantCulture)},{(scale ?? 0).ToString(CultureInfo.InvariantCulture)})";
            }

            // Null precision with a scale is an integer NUMBER(*, s) (INTEGER is NUMBER(*, 0)); Oracle caps its
            // integer precision at 38, so render the maximum so it maps to a fixed decimal, not a lossy float.
            // Null precision AND null scale is a truly unconstrained, floating NUMBER.
            return scale is { } s ? $"NUMBER(38,{s.ToString(CultureInfo.InvariantCulture)})" : "NUMBER";
        }

        if (type == "FLOAT")
        {
            return precision is { } fp ? $"FLOAT({fp.ToString(CultureInfo.InvariantCulture)})" : "FLOAT";
        }

        if (type is "VARCHAR2" or "NVARCHAR2" or "VARCHAR" or "CHAR" or "NCHAR")
        {
            var length = charLength is > 0 ? charLength : dataLength;
            return length is { } l ? $"{type}({l.ToString(CultureInfo.InvariantCulture)})" : type;
        }

        if (type == "RAW")
        {
            return dataLength is { } rl ? $"RAW({rl.ToString(CultureInfo.InvariantCulture)})" : "RAW";
        }

        return dataType;
    }

    private static void AddObjectFilters(OracleCommand command, string? owner, CatalogQuery query)
    {
        AddParameter(command, "owner", owner);
        AddParameter(command, "tables", query.IncludeTables ? 1 : 0);
        AddParameter(command, "views", query.IncludeViews ? 1 : 0);
        AddParameter(command, "namelike", query.NameLike);
    }

    // Bind by name so the same placeholder used more than once in a statement resolves to one parameter, and
    // parameter order does not have to track the SQL text.
    private static OracleCommand CreateCommand(DbConnection connection)
    {
        var command = (OracleCommand)connection.CreateCommand();
        command.BindByName = true;
        // Dictionary queries on a busy instance can outlast the provider's default client timeout; the server
        // decides when they finish, so a slow catalog read degrades throughput instead of failing the flow.
        command.CommandTimeout = 0;
        return command;
    }

    private static void AddParameter(OracleCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
