using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Compute;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The outcome of working out which datasource a query belongs to. <see cref="Reference"/> is set only when the
/// evidence points at exactly one declared datasource; <see cref="Candidates"/> lists every declared datasource
/// the evidence reached, so a caller that has to ask a person can say why (none found, or several).
/// </summary>
/// <param name="Reference">The one datasource the query runs against, or null when it is not unambiguous.</param>
/// <param name="Candidates">Every declared datasource the query's objects were found on, best known casing.</param>
/// <param name="Database">The database the query's objects live in, when a schema registration flow registered
/// them all in one database on <see cref="Reference"/>; null otherwise (the connection's own database applies).</param>
public sealed record DatasourceInferenceResult(string? Reference, IReadOnlyList<string> Candidates, string? Database = null);

/// <summary>
/// Works out which datasource a query runs against from what the catalog already knows, so a person is not asked
/// to pick a connection for a question whose tables already say where they live (POWERAI.md Section 6). This is
/// the ONE place that decision is made: question retrieval, confirming an example, auto-run, and preparing an
/// ad-hoc query all call it rather than each guessing in its own way.
/// <para>
/// Three kinds of evidence, in order. First the schema registration flows (flowType: sch) that registered the
/// objects: such an object is metadata only, and the registering pipeline's source server is the one place its data
/// can be fetched from, in the database it was registered in. Then the object keys the caller already holds (a
/// confirmed example's, or a PowerBI visual's query, whose model entities the lineage pass resolved to warehouse
/// objects): a node key's first segment IS the connection reference the object was reached through. Only when those
/// name no declared datasource is the SQL itself parsed and its tables looked up in the catalog by schema/name, the
/// same way <see cref="ColumnPolicyGuard"/> resolves them, and the same two steps are applied to what it finds.
/// </para>
/// <para>
/// The answer is only ever a datasource some active pipeline declares as its source or target, which is the same
/// known-reference gate prepare and confirm enforce, so inference can never point a query at a connection the
/// reviewed estate does not use. It never guesses between several: a table known under two references (read by
/// one flow, written by another) yields both as candidates and no reference, and the caller asks.
/// </para>
/// </summary>
public static class DatasourceInference
{
    /// <summary>Infers the datasource for <paramref name="sql"/>, preferring <paramref name="objectKeys"/> when
    /// they resolve.</summary>
    /// <param name="db">The catalog.</param>
    /// <param name="sql">The query; may be empty when only object keys are known.</param>
    /// <param name="objectKeys">Lineage node keys the query reads, when the caller has them.</param>
    /// <param name="ct">Cancels the lookups.</param>
    public static async Task<DatasourceInferenceResult> InferAsync(
        CatalogDbContext db, string? sql, IEnumerable<string>? objectKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var declared = await DeclaredReferencesAsync(db, ct).ConfigureAwait(false);
        if (declared.Count == 0)
        {
            return new DatasourceInferenceResult(null, []);
        }

        var keys = (objectKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList();
        if (await FromRegistrationsAsync(db, declared, keys, ct).ConfigureAwait(false) is { } registered)
        {
            return registered;
        }

        var fromKeys = Resolve(declared, ServersFromKeys(keys));
        if (fromKeys.Count > 0)
        {
            return Result(fromKeys);
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new DatasourceInferenceResult(null, []);
        }

        var objects = await ObjectsFromSqlAsync(db, sql, ct).ConfigureAwait(false);
        if (await FromRegistrationsAsync(db, declared, objects.Select(o => o.Key).ToList(), ct).ConfigureAwait(false)
            is { } registeredFromSql)
        {
            return registeredFromSql;
        }

        return Result(Resolve(declared, objects.Select(o => o.ServerRef)));
    }

    /// <summary>Whether <paramref name="reference"/> is a datasource the reviewed estate declares: the one gate every
    /// surface that runs or stores a query applies, so none of them can point a worker at a novel connection. An
    /// <c>@alias</c> is not decided here; it resolves only against the node's curated registry.</summary>
    public static async Task<bool> IsDeclaredAsync(CatalogDbContext db, string reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return (await DeclaredReferencesAsync(db, ct).ConfigureAwait(false)).ContainsKey(reference.Trim());
    }

    /// <summary>
    /// The datasource the registering schema registration flows point at, for the objects among
    /// <paramref name="objectKeys"/> that an active one registered; null when none of them is registered. Several
    /// registering connections leave the reference open with them as candidates, and the database is named only when
    /// every registered object lives in the same one.
    /// </summary>
    private static async Task<DatasourceInferenceResult?> FromRegistrationsAsync(
        CatalogDbContext db, IReadOnlyDictionary<string, string> declared, IReadOnlyList<string> objectKeys,
        CancellationToken ct)
    {
        if (objectKeys.Count == 0)
        {
            return null;
        }

        var registersRelation = nameof(Core.Lineage.LineageRelation.Registers);
        var rows = new List<(string SourceServer, string? Database)>();
        foreach (var chunk in objectKeys.Distinct(StringComparer.Ordinal).Chunk(RegistrationChunk))
        {
            var found = await (
                    from edge in db.LineageEdges.AsNoTracking()
                    where edge.Relation == registersRelation && edge.PipelineId != null && chunk.Contains(edge.ObjectKey)
                    join pipeline in db.Pipelines.AsNoTracking() on edge.PipelineId equals pipeline.Id
                    where pipeline.Active && pipeline.SourceServer != null
                    join item in db.Objects.AsNoTracking() on edge.ObjectKey equals item.Key
                    select new { pipeline.SourceServer, item.Database })
                .ToListAsync(ct).ConfigureAwait(false);
            rows.AddRange(found.Select(r => (r.SourceServer!, (string?)r.Database)));
        }

        if (rows.Count == 0)
        {
            return null;
        }

        var candidates = Resolve(declared, rows.Select(r => r.SourceServer));
        if (candidates.Count != 1)
        {
            return new DatasourceInferenceResult(null, candidates);
        }

        var databases = rows
            .Select(r => r.Database)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DatasourceInferenceResult(candidates[0], candidates, databases.Count == 1 ? databases[0] : null);
    }

    /// <summary>How many object keys one registration lookup binds, well under SQL Server's parameter limit.</summary>
    private const int RegistrationChunk = 500;

    /// <summary>The keys of the catalog objects <paramref name="sql"/>'s tables resolve to, by the same schema/name
    /// lookup inference uses, so a question example stored without caller-supplied keys is still tied to the tables
    /// it reads. Empty when the statement is not one SELECT or names no catalogued table.</summary>
    /// <param name="db">The catalog.</param>
    /// <param name="sql">The query.</param>
    /// <param name="ct">Cancels the lookup.</param>
    public static async Task<IReadOnlyList<string>> ObjectKeysFromSqlAsync(
        CatalogDbContext db, string sql, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        var objects = await ObjectsFromSqlAsync(db, sql, ct).ConfigureAwait(false);
        return objects.Select(o => o.Key).Distinct(StringComparer.Ordinal).ToList();
    }

    private static DatasourceInferenceResult Result(IReadOnlyList<string> candidates)
        => new(candidates.Count == 1 ? candidates[0] : null, candidates);

    /// <summary>Every whole reference an active pipeline declares, keyed case-insensitively because a node key
    /// lower-cases its server segment while the pipeline row keeps the reference as written.</summary>
    private static async Task<Dictionary<string, string>> DeclaredReferencesAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Select(p => new { p.SourceServer, p.TargetServer })
            .ToListAsync(ct).ConfigureAwait(false);

        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in rows.SelectMany(r => new[] { r.SourceServer, r.TargetServer }))
        {
            if (!string.IsNullOrWhiteSpace(reference) && ComputeTaskPayload.IsWholeReference(reference))
            {
                declared.TryAdd(reference.Trim(), reference.Trim());
            }
        }

        return declared;
    }

    /// <summary>The server segment of each node key (<c>server|database|schema|name</c>).</summary>
    private static IEnumerable<string> ServersFromKeys(IEnumerable<string>? objectKeys)
        => (objectKeys ?? [])
            .Select(key => key?.Trim() ?? string.Empty)
            .Select(key => key.IndexOf('|', StringComparison.Ordinal) is var bar and > 0 ? key[..bar] : string.Empty)
            .Where(server => server.Length > 0);

    /// <summary>A catalog object a query's table resolved to: its node key and the connection reference it was
    /// reached through.</summary>
    private sealed record ResolvedObject(string Key, string ServerRef);

    /// <summary>The catalog objects a query's tables resolve to. A statement that does not parse as one SELECT
    /// yields nothing, since there is then no table list to trust.</summary>
    private static async Task<IReadOnlyList<ResolvedObject>> ObjectsFromSqlAsync(
        CatalogDbContext db, string sql, CancellationToken ct)
    {
        var select = ColumnPolicyGuard.ParseSingleSelect(sql);
        if (select is null)
        {
            return [];
        }

        var cteNames = (select.WithCtesAndXmlNamespaces?.CommonTableExpressions ?? [])
            .Select(c => c.ExpressionName.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = SqlColumnAccessExtractor.ExtractTables(select)
            .Where(t => !cteNames.Contains(t.Name))
            .ToList();
        if (tables.Count == 0)
        {
            return [];
        }

        var names = tables.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var objects = await db.Objects.AsNoTracking()
            .Where(o => names.Contains(o.Name))
            .Select(o => new { o.Key, o.ServerRef, o.Schema, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        return objects
            .Where(o => tables.Exists(t => t.Matches(o.Schema, o.Name)))
            .Select(o => new ResolvedObject(o.Key, o.ServerRef))
            .ToList();
    }

    private static IReadOnlyList<string> Resolve(
        IReadOnlyDictionary<string, string> declared, IEnumerable<string> servers)
        => servers
            .Select(server => declared.TryGetValue(server, out var reference) ? reference : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
