using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// Phases one and two of the derived tier, connected: per SQL Server the documents reference, one read-only
/// catalog inventory (objects, synonyms) and one verbatim module harvest (sys.sql_modules), each module's
/// definition parsed through the same operation-wise extractor. This is the SMO replacement: plain set-based
/// reads, one parser instance per module, no shared state, servers processed in parallel and each server's
/// module bodies parsed in parallel (bounded by the processor count). An unreachable server or an encrypted
/// module degrades to a warning, never a failure: the derived tier is an enrichment of the offline graph,
/// not a prerequisite for it.
/// </summary>
public sealed class CatalogCollector
{
    private readonly IConnectionResolver _resolver;
    private readonly Func<string, CancellationToken, Task>? _progress;

    public CatalogCollector(IConnectionResolver resolver, Func<string, CancellationToken, Task>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _progress = progress;
    }

    private Task ReportAsync(string message, CancellationToken ct)
        => _progress is null ? Task.CompletedTask : _progress(message, ct);

    public async Task<CollectionResult> CollectAsync(
        IReadOnlyDictionary<string, (string RawReference, DataSourceKind Kind)> servers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var sqlServers = servers
            .Where(s => s.Value.Kind is DataSourceKind.MSSQL or DataSourceKind.AZDB)
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();

        var merged = new CollectionResult();

        // Identity proof pass: two references resolving to the same canonical connection string ARE the
        // same server. The ordinal-smallest identity represents the group; the rest alias to it, so the
        // graph stops splitting one physical estate across reference spellings. The resolved canonical
        // string is carried into the collection below, so each server's reference (and its secret) is
        // resolved exactly once per refresh.
        var representatives = new List<(string ServerRef, string ConnectionString)>();
        var byCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (serverRef, value) in sqlServers)
        {
            string canonical;
            try
            {
                canonical = (await _resolver.ResolveAsync(value.RawReference, ConnectionRole.Source, ct: ct).ConfigureAwait(false)).CanonicalString;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = Core.Secrets.SecretHygiene.RedactedMessage(ex);
                merged.Warnings.Add($"server '{serverRef}': derived lineage unavailable ({reason}); the offline tiers still apply.");
                merged.DegradedServers.Add(serverRef);
                await ReportAsync($"derived tier: server '{serverRef}' FAILED to resolve its connection ({reason}); previously-derived lineage for it is preserved.", ct).ConfigureAwait(false);
                continue;
            }

            if (byCanonical.TryGetValue(canonical, out var representative))
            {
                merged.ServerAliases[serverRef] = representative;
            }
            else
            {
                byCanonical[canonical] = serverRef;
                representatives.Add((serverRef, canonical));
            }
        }

        var results = await Task.WhenAll(representatives.Select(server => CollectServerAsync(server.ServerRef, server.ConnectionString, ct)))
            .ConfigureAwait(false);

        foreach (var result in results)
        {
            merged.Merge(result);
        }

        foreach (var skipped in servers.Where(s => s.Value.Kind is not (DataSourceKind.MSSQL or DataSourceKind.AZDB)))
        {
            if (skipped.Key != ServerIdentity.FileSystem)
            {
                merged.Warnings.Add(
                    $"server '{skipped.Key}' is {skipped.Value.Kind}; module-level lineage derivation covers SQL Server only.");
            }
        }

        return merged;
    }

    private async Task<CollectionResult> CollectServerAsync(string serverRef, string connectionString, CancellationToken ct)
    {
        var result = new CollectionResult();
        try
        {
            await ReportAsync($"derived tier: server '{serverRef}': connecting.", ct).ConfigureAwait(false);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var database = (string?)await Scalar(connection, "SELECT DB_NAME();", ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the connection has no default database");

            // The connected default catalog is node-identity ground truth: the builder completes this
            // server's two-part identities (database-less facts) against it.
            result.ServerDefaultDatabases[serverRef] = database;

            await InventoryAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            await SynonymsAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            await ModulesAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            await TableScriptsAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);

            var modules = result.Facts
                .Where(f => f.ViaModuleKey is not null)
                .Select(f => f.ViaModuleKey!)
                .Distinct(StringComparer.Ordinal)
                .Count();
            await ReportAsync(
                $"derived tier: server '{serverRef}' (db '{database}'): {result.CatalogObjects.Count} object(s) inventoried, {modules} module bodies parsed.",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = Core.Secrets.SecretHygiene.RedactedMessage(ex);
            result.Warnings.Add($"server '{serverRef}': derived lineage unavailable ({reason}); the offline tiers still apply.");
            result.DegradedServers.Add(serverRef);
            await ReportAsync($"derived tier: server '{serverRef}' FAILED ({reason}); previously-derived lineage for it is preserved.", ct).ConfigureAwait(false);
        }

        return result;
    }

    private static async Task InventoryAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        var columnsByObject = await ColumnsAsync(connection, ct).ConfigureAwait(false);

        const string sql = """
            SELECT s.name, o.name, o.type
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','FN','IF','TF','TR') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = reader.GetString(2).TrimEnd() switch
                {
                    "U" => LineageNodeKind.Table,
                    "V" => LineageNodeKind.View,
                    "P" => LineageNodeKind.Procedure,
                    "FN" or "IF" or "TF" => LineageNodeKind.Function,
                    "TR" => LineageNodeKind.Trigger,
                    _ => LineageNodeKind.Unknown,
                },
                Columns = columnsByObject.TryGetValue(schema + "|" + name, out var columns) ? columns : [],
            });
        }
    }

    /// <summary>The columns of every table/view/table-valued function, keyed by <c>schema|name</c>
    /// (case-insensitive, matching SQL Server's default object-name collation), so the inventory can attach a
    /// data dictionary to each object for cross-repo column search.</summary>
    private static async Task<Dictionary<string, List<LineageColumn>>> ColumnsAsync(SqlConnection connection, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, o.name, c.column_id, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V','IF','TF') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id;
            """;

        var map = new Dictionary<string, List<LineageColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = reader.GetString(0) + "|" + reader.GetString(1);
            if (!map.TryGetValue(key, out var columns))
            {
                map[key] = columns = [];
            }

            columns.Add(new LineageColumn
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
    /// Reconstructs a <c>CREATE TABLE</c> script for every base table from the live schema - columns (with
    /// identity, computed expressions, nullability) and the primary key - and attaches it as a derived-tier
    /// artifact. SQL Server keeps no CREATE TABLE text (unlike a module's <c>sys.sql_modules</c> body), so the
    /// catalog would otherwise hold a script for views/procedures but never for tables. A generating script per
    /// object is required to recreate the estate in a new environment and to reason about it offline, so tables
    /// are scripted here the way <see cref="ModulesAsync"/> harvests module bodies.
    /// </summary>
    private static async Task TableScriptsAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        const string columnsSql = """
            SELECT s.name, o.name, c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   CONVERT(bigint, ISNULL(ic.seed_value, 1)), CONVERT(bigint, ISNULL(ic.increment_value, 1)),
                   c.is_computed, cc.definition
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE o.type = 'U' AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, c.column_id;
            """;

        var tables = new Dictionary<string, TableScript>(StringComparer.Ordinal);
        TableScript Table(string schema, string name)
        {
            var key = schema + "|" + name;
            if (!tables.TryGetValue(key, out var t))
            {
                tables[key] = t = new TableScript { Schema = schema, Name = name };
            }

            return t;
        }

        await using (var command = new SqlCommand(columnsSql, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
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

        const string pkSql = """
            SELECT s.name, o.name, kc.name, i.type_desc, col.name
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
            WHERE i.is_primary_key = 1 AND o.type = 'U' AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name, ic.key_ordinal;
            """;

        await using (var command = new SqlCommand(pkSql, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var table = Table(reader.GetString(0), reader.GetString(1));
                table.PrimaryKeyName = reader.GetString(2);
                table.PrimaryKeyClustered = reader.GetString(3);   // CLUSTERED / NONCLUSTERED
                table.PrimaryKeyColumns.Add(reader.GetString(4));
            }
        }

        foreach (var table in tables.Values)
        {
            var lines = new List<string>(table.Columns);
            if (table.PrimaryKeyColumns.Count > 0)
            {
                var keyColumns = string.Join(", ", table.PrimaryKeyColumns.Select(c => $"[{c}] ASC"));
                lines.Add($"    CONSTRAINT [{table.PrimaryKeyName}] PRIMARY KEY {table.PrimaryKeyClustered} ({keyColumns})");
            }

            result.ObjectArtifacts.Add(new CollectedObjectArtifact
            {
                ServerRef = serverRef,
                Database = database,
                Schema = table.Schema,
                Name = table.Name,
                Kind = LineageNodeKind.Table,
                Script = $"CREATE TABLE [{table.Schema}].[{table.Name}] (\n{string.Join(",\n", lines)}\n);",
                Tier = LineageTier.Derived,
            });
        }
    }

    /// <summary>Accumulates one base table's rendered column lines and primary key while the schema is read.</summary>
    private sealed class TableScript
    {
        public required string Schema { get; init; }

        public required string Name { get; init; }

        public List<string> Columns { get; } = [];

        public string? PrimaryKeyName { get; set; }

        public string PrimaryKeyClustered { get; set; } = "CLUSTERED";

        public List<string> PrimaryKeyColumns { get; } = [];
    }

    /// <summary>Renders a SQL Server type with its length/precision the way it reads in DDL (best-effort, for
    /// display and search): <c>nvarchar(100)</c>, <c>nvarchar(max)</c>, <c>decimal(18,2)</c>, <c>datetime2(7)</c>.</summary>
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

    private static async Task SynonymsAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        // PARSENAME splits the base name server-side; a four-part (linked server) base comes back with a
        // server part and is surfaced as unresolvable rather than guessed at.
        const string sql = """
            SELECT s.name, sy.name,
                   PARSENAME(sy.base_object_name, 1), PARSENAME(sy.base_object_name, 2),
                   PARSENAME(sy.base_object_name, 3), PARSENAME(sy.base_object_name, 4)
            FROM sys.synonyms sy
            JOIN sys.schemas s ON s.schema_id = sy.schema_id
            ORDER BY s.name, sy.name;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var baseName = reader.IsDBNull(2) ? null : reader.GetString(2);
            var linkedServer = reader.IsDBNull(5) ? null : reader.GetString(5);

            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = LineageNodeKind.Synonym,
                Warning = linkedServer is null
                    ? null
                    : $"synonym base lives on linked server '{linkedServer}'; not resolvable from here.",
            });

            if (baseName is not null && linkedServer is null)
            {
                result.Synonyms.Add(new SynonymLink
                {
                    ServerRef = serverRef,
                    Database = database,
                    Schema = schema,
                    Name = name,
                    TargetDatabase = reader.IsDBNull(4) ? database : reader.GetString(4),
                    TargetSchema = reader.IsDBNull(3) ? null : reader.GetString(3),
                    TargetName = baseName,
                });
            }
        }
    }

    private static async Task ModulesAsync(
        CollectionResult result, SqlConnection connection, string serverRef, string database, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, o.name, m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;

        var modules = new List<(string Schema, string Name, string? Definition)>();
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                modules.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        // The ScriptDom walk is pure CPU and the extractor is thread-safe by construction (one parser and one
        // walk state per call, no shared mutable state), so the readable bodies parse in parallel, bounded by
        // the processor count. Each module's warnings and facts land in its own slot; the sequential merge
        // below runs in the original catalog order, so the output is byte-for-byte what the serial walk built.
        var extracted = new (List<string> Warnings, List<LineageFact> Facts, Extraction.ScriptDependencies Deps)[modules.Count];
        Parallel.For(
            0,
            modules.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            i =>
            {
                var (schema, name, definition) = modules[i];
                if (definition is null)
                {
                    return;
                }

                var moduleKey = NodeKey.For(serverRef, database, schema, name);
                var label = $"{serverRef}:{database}.{schema}.{name}";
                var deps = Extraction.TSqlLineageExtractor.Extract(definition, label, defaultDatabase: database);

                var facts = new List<LineageFact>();
                foreach (var fact in ScriptFactBuilder.Facts(
                             deps, flow: null, viaModuleKey: moduleKey, serverRef, LineageTier.Derived, minimumParts: 1))
                {
                    // The module's own CREATE statement points at itself; self-facts carry nothing.
                    if (NodeKey.For(serverRef, fact.Database ?? database, fact.Schema, fact.Name) != moduleKey)
                    {
                        facts.Add(fact with { Database = fact.Database ?? database });
                    }
                }

                extracted[i] = (deps.Warnings, facts, deps);
            });

        for (var i = 0; i < modules.Count; i++)
        {
            var (schema, name, definition) = modules[i];
            if (definition is null)
            {
                // WITH ENCRYPTION: the stored text is unreadable by design; the gap is declared, not hidden.
                result.CatalogObjects.Add(new CatalogObject
                {
                    ServerRef = serverRef,
                    Database = database,
                    Schema = schema,
                    Name = name,
                    Kind = LineageNodeKind.Unknown,
                    Warning = "module is encrypted (WITH ENCRYPTION); its definition cannot be read, so its lineage is unknown.",
                });
                continue;
            }

            // Retain the module body so the catalog is searchable code (the kind is merged in from the inventory
            // entry; this entry contributes only the definition).
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = database,
                Schema = schema,
                Name = name,
                Kind = LineageNodeKind.Unknown,
                Definition = definition,
            });

            result.Warnings.AddRange(extracted[i].Warnings);
            result.Facts.AddRange(extracted[i].Facts);

            // The module body is real, curated codebase SQL: its joins and constraint clauses are
            // data-model observations of the derived tier, attributed to the module as the script unit.
            ScriptFactBuilder.AppendModelObservations(
                result, extracted[i].Deps, serverRef, LineageTier.Derived,
                NodeKey.For(serverRef, database, schema, name));
        }
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
