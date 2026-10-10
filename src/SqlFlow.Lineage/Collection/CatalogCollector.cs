using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Lineage;
using SqlFlow.SqlServer.Catalog;

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

            var harvest = await SqlServerObjectHarvester.HarvestAsync(
                connection, new ObjectHarvestRequest { IncludeProgrammability = true }, ct).ConfigureAwait(false);
            var database = harvest.Database;

            // The connected default catalog is node-identity ground truth: the builder completes this
            // server's two-part identities (database-less facts) against it.
            result.ServerDefaultDatabases[serverRef] = database;

            AddInventory(result, harvest, serverRef);
            await SynonymsAsync(result, connection, serverRef, database, ct).ConfigureAwait(false);
            AddModules(result, harvest, serverRef, ct);

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

    /// <summary>
    /// The harvested inventory as catalog objects (with their columns) plus a derived-tier script artifact per base
    /// table: SQL Server keeps no CREATE TABLE text (unlike a module's <c>sys.sql_modules</c> body), so the catalog
    /// would otherwise hold a script for views and procedures but never for tables.
    /// </summary>
    private static void AddInventory(CollectionResult result, ObjectHarvest harvest, string serverRef)
    {
        foreach (var item in harvest.Objects)
        {
            result.CatalogObjects.Add(new CatalogObject
            {
                ServerRef = serverRef,
                Database = harvest.Database,
                Schema = item.Schema,
                Name = item.Name,
                Kind = item.Kind,
                Columns = item.Columns,
            });

            if (item.TableScript is not null)
            {
                result.ObjectArtifacts.Add(new CollectedObjectArtifact
                {
                    ServerRef = serverRef,
                    Database = harvest.Database,
                    Schema = item.Schema,
                    Name = item.Name,
                    Kind = LineageNodeKind.Table,
                    Script = item.TableScript,
                    Tier = LineageTier.Derived,
                });
            }
        }
    }

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

    private static void AddModules(
        CollectionResult result, ObjectHarvest harvest, string serverRef, CancellationToken ct)
    {
        var database = harvest.Database;
        var modules = harvest.Modules.Select(m => (m.Schema, m.Name, m.Definition)).ToList();

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
}
