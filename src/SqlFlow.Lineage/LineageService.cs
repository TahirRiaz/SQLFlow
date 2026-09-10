using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.SqlServer;

namespace SqlFlow.Lineage;

/// <summary>How a lineage computation runs: which folder, which tiers.</summary>
public sealed record LineageOptions
{
    public required string FlowDirectory { get; init; }

    /// <summary>Read the canonical run artifacts (offline ground truth). Default on.</summary>
    public bool IncludeObserved { get; init; } = true;

    /// <summary>Connect to the referenced SQL Servers and expand modules (needs connectivity). Default off:
    /// the offline tiers must always stand on their own.</summary>
    public bool IncludeDerived { get; init; }

    /// <summary>Secret resolution for connection references; environment variables when null.</summary>
    public ISecretResolver? Secrets { get; init; }

    /// <summary>Under-the-hood progress, one human-readable line per step (tier begins, each server's connect /
    /// harvest tally / failure), invoked as the computation runs. The managed sync streams these into the
    /// activity trace the GUI panel tails; the CLI prints them. Callbacks may arrive concurrently (servers are
    /// collected in parallel), so the sink must serialize itself. Null: silent.</summary>
    public Func<string, CancellationToken, Task>? Progress { get; init; }
}

/// <summary>The report plus its raw pre-merge facts: what
/// <see cref="LineageService.ComputeDetailedAsync(LineageOptions, CancellationToken)"/> returns when a caller
/// wants both the canonical graph and the debugging dump from one collection pass.</summary>
public sealed record LineageComputation
{
    public required LineageReport Report { get; init; }

    public required LineageFactsDump Facts { get; init; }
}

/// <summary>
/// The lineage front door: runs the three phases (collect, script, calculate) for the requested tiers and
/// returns the canonical report. Both the CLI and the tests construct through here, so lineage has exactly
/// one code path; the connectivity posture is the only choice.
/// </summary>
public static class LineageService
{
    public static async Task<LineageReport> ComputeAsync(LineageOptions options, CancellationToken ct = default)
        => (await ComputeDetailedAsync(options, ct).ConfigureAwait(false)).Report;

    /// <summary>Computes the report from an already-collected declared-tier flow set, so a caller that has just
    /// scanned the estate (the catalog sync) never scans or parses the documents a second time. Same contract as
    /// <see cref="ComputeAsync(LineageOptions, CancellationToken)"/> otherwise; see
    /// <see cref="ComputeDetailedAsync(LineageOptions, CollectionResult, CancellationToken)"/> for the mutation
    /// note on <paramref name="collected"/>.</summary>
    public static async Task<LineageReport> ComputeAsync(
        LineageOptions options, CollectionResult collected, CancellationToken ct = default)
        => (await ComputeDetailedAsync(options, collected, ct).ConfigureAwait(false)).Report;

    /// <summary>
    /// Runs the collection once and returns BOTH the canonical report AND the raw pre-merge facts (the dump),
    /// so a caller that wants the debugging view does not collect twice (and never connects twice). The report
    /// and the dump carry the same GeneratedAtUtc and tier set.
    /// </summary>
    public static Task<LineageComputation> ComputeDetailedAsync(LineageOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ComputeDetailedAsync(options, new FlowSetCollector().Collect(options.FlowDirectory), ct);
    }

    /// <summary>
    /// The pre-collected variant of <see cref="ComputeDetailedAsync(LineageOptions, CancellationToken)"/>: the
    /// caller supplies the declared-tier collection (typically the very scan it already ran over
    /// <see cref="LineageOptions.FlowDirectory"/>), and only the requested extra tiers are collected here. The
    /// observed and derived tiers merge INTO <paramref name="collected"/>, so the instance is mutated; pass a
    /// result you do not need to keep pristine.
    /// </summary>
    public static async Task<LineageComputation> ComputeDetailedAsync(
        LineageOptions options, CollectionResult collected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(collected);

        var progress = options.Progress;
        var tiers = new List<LineageTier> { LineageTier.Declared };
        if (progress is not null)
        {
            await progress($"declared tier: {collected.Flows.Count} flow(s), {collected.Facts.Count} fact(s) from the YAML estate.", ct).ConfigureAwait(false);
        }

        if (options.IncludeObserved)
        {
            var factsBefore = collected.Facts.Count;
            collected.Merge(RunArtifactCollector.Collect(options.FlowDirectory, collected.Flows));
            tiers.Add(LineageTier.Observed);
            if (progress is not null)
            {
                await progress($"observed tier: {collected.Facts.Count - factsBefore} fact(s) from run artifacts.", ct).ConfigureAwait(false);
            }
        }

        var resolver = WithoutDatabaseResolver.Build([], options.Secrets, SqlServerSourceProvider.CreateRegistry());
        if (options.IncludeDerived)
        {
            if (progress is not null)
            {
                await progress("derived tier: connecting to the referenced servers to harvest object code.", ct).ConfigureAwait(false);
            }

            collected.Merge(await new CatalogCollector(resolver, progress).CollectAsync(collected.Servers, ct).ConfigureAwait(false));
            tiers.Add(LineageTier.Derived);
        }

        // Default-database resolution, after every tier has contributed facts: the connected pass above
        // recorded DB_NAME() ground truth for the servers it reached; this fills the remaining servers
        // offline from each reference's Initial Catalog, so a two-part identity (a file-flow target, an
        // observed statement) resolves against the same catalog the engine executes it in.
        await DefaultDatabaseResolver.ResolveAsync(collected, resolver, ct).ConfigureAwait(false);

        var flowDirectory = Path.GetFullPath(options.FlowDirectory);
        var generatedAtUtc = DateTime.UtcNow;

        // The dump is projected from the raw collected facts BEFORE the builder's identity unification and
        // synonym resolution, so it shows exactly what each tier contributed.
        var facts = BuildFactsDump(collected, flowDirectory, tiers, generatedAtUtc);
        var report = LineageGraphBuilder.Build(collected, flowDirectory, tiers, generatedAtUtc);
        return new LineageComputation { Report = report, Facts = facts };
    }

    /// <summary>Projects the raw collection into the secret-safe, deterministically-ordered facts dump.</summary>
    private static LineageFactsDump BuildFactsDump(
        CollectionResult collected, string flowDirectory, IReadOnlyList<LineageTier> tiers, DateTime generatedAtUtc)
    {
        var facts = collected.Facts
            .Select(f => new LineageRawFact
            {
                Flow = f.Flow,
                ViaModule = f.ViaModuleKey,
                Relation = f.Relation,
                ServerRef = f.ServerRef,
                Database = f.Database,
                Schema = f.Schema,
                Name = f.Name,
                NodeKey = NodeKey.For(f.ServerRef, f.Database, f.Schema, f.Name),
                Tier = f.Tier,
                Kind = f.KindHint,
                ObservedRunId = f.RunId,
                ObservedAtUtc = f.ObservedAtUtc,
                Step = f.Step,
            })
            .OrderBy(f => f.Tier)
            .ThenBy(f => f.Flow ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ViaModule ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(f => f.NodeKey, StringComparer.Ordinal)
            .ThenBy(f => f.Relation)
            .ToList();

        var catalog = collected.CatalogObjects
            .Select(o => new LineageDumpCatalogObject
            {
                ServerRef = o.ServerRef, Database = o.Database, Schema = o.Schema, Name = o.Name, Kind = o.Kind, Warning = o.Warning,
            })
            .OrderBy(o => o.ServerRef, StringComparer.Ordinal).ThenBy(o => o.Database, StringComparer.Ordinal)
            .ThenBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal)
            .ToList();

        var synonyms = collected.Synonyms
            .Select(s => new LineageDumpSynonym
            {
                ServerRef = s.ServerRef, Database = s.Database, Schema = s.Schema, Name = s.Name,
                TargetDatabase = s.TargetDatabase, TargetSchema = s.TargetSchema, TargetName = s.TargetName,
            })
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal).ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var servers = collected.Servers
            .Select(kvp => new LineageDumpServer
            {
                Identity = kvp.Key,
                Reference = RedactReference(kvp.Value.RawReference),
                Kind = kvp.Value.Kind,
            })
            .OrderBy(s => s.Identity, StringComparer.Ordinal)
            .ToList();

        return new LineageFactsDump
        {
            GeneratedAtUtc = generatedAtUtc,
            FlowDirectory = flowDirectory,
            TiersUsed = tiers,
            Facts = facts,
            CatalogObjects = catalog,
            Synonyms = synonyms,
            Servers = servers,
            ServerAliases = collected.ServerAliases.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
            Warnings = collected.Warnings.Distinct(StringComparer.Ordinal).OrderBy(w => w, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>A whole <c>${...}</c>/<c>@alias</c> reference is safe to echo; an inline literal may carry a
    /// credential and is never written to the dump, only flagged.</summary>
    private static string RedactReference(string reference)
    {
        var trimmed = reference.Trim();
        var isWholeReference =
            (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
             && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1)
            || (trimmed.StartsWith('@') && !trimmed.Any(char.IsWhiteSpace)
                && !trimmed.Contains(';', StringComparison.Ordinal) && !trimmed.Contains('=', StringComparison.Ordinal));
        return isWholeReference ? trimmed : "(inline literal redacted)";
    }

    /// <summary>
    /// Explains one flow's place in the execution plan, purely from the report: its wave, whether it is in a
    /// cycle, the upstream flows it waits for (with the mediating objects and their waves), the downstream flows
    /// that wait for it, and the objects it reads and writes with tier provenance. Returns null when the name is
    /// not a flow in the graph.
    /// </summary>
    public static FlowExplanation? ExplainFlow(LineageReport report, string flowName)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);

        var flow = report.Flows.FirstOrDefault(f => string.Equals(f.Name, flowName, StringComparison.OrdinalIgnoreCase));
        if (flow is null)
        {
            return null;
        }

        var name = flow.Name;

        int WaveOf(string member) => report.ExecutionPlan.Waves
            .FirstOrDefault(w => w.Flows.Any(f => string.Equals(f, member, StringComparison.OrdinalIgnoreCase)))?.Wave ?? 0;

        var inCycle = report.ExecutionPlan.Unordered.Any(u => string.Equals(u, name, StringComparison.OrdinalIgnoreCase))
                      || report.Cycles.Any(c => c.Flows.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)));

        var dependsOn = report.FlowDependencies
            .Where(d => string.Equals(d.ToFlow, name, StringComparison.OrdinalIgnoreCase))
            .Select(d => new ExplainDependency { Flow = d.FromFlow, Wave = WaveOf(d.FromFlow), ViaObjects = d.ViaObjects })
            .OrderBy(d => d.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var requiredBy = report.FlowDependencies
            .Where(d => string.Equals(d.FromFlow, name, StringComparison.OrdinalIgnoreCase))
            .Select(d => new ExplainDependency { Flow = d.ToFlow, Wave = WaveOf(d.ToFlow), ViaObjects = d.ViaObjects })
            .OrderBy(d => d.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ownEdges = report.Edges.Where(e => string.Equals(e.Flow, name, StringComparison.OrdinalIgnoreCase)).ToList();
        ExplainEdge Map(LineageEdge e) => new()
        {
            ObjectName = e.ObjectKey, Relation = e.Relation, Tier = e.Tier, ObservedRunId = e.ObservedRunId, Step = e.Step,
        };

        var reads = ownEdges.Where(e => e.Relation is LineageRelation.Reads or LineageRelation.Requires)
            .Select(Map).OrderBy(e => e.ObjectName, StringComparer.Ordinal).ThenBy(e => e.Relation).ToList();
        var writes = ownEdges.Where(e => e.Relation is LineageRelation.Writes or LineageRelation.Creates or LineageRelation.Destroys)
            .Select(Map).OrderBy(e => e.ObjectName, StringComparer.Ordinal).ThenBy(e => e.Relation).ToList();

        return new FlowExplanation
        {
            Flow = name,
            Kind = flow.Kind,
            Wave = WaveOf(name),
            InCycle = inCycle,
            DependsOn = dependsOn,
            RequiredBy = requiredBy,
            Reads = reads,
            Writes = writes,
        };
    }

    /// <summary>Everything downstream of a subject (an object key or a flow name): what an incident at the
    /// subject impacts. Data-flow direction: writer flow to object, object to reading flow, base object to
    /// the module that reads it.</summary>
    public static IReadOnlyList<string> Downstream(LineageReport report, string subject)
        => Traverse(report, subject, downstream: true);

    /// <summary>Everything upstream of a subject: what it depends on.</summary>
    public static IReadOnlyList<string> Upstream(LineageReport report, string subject)
        => Traverse(report, subject, downstream: false);

    /// <summary>Resolves a user-supplied subject (a flow name, a full node key, or a name/schema.name/
    /// db.schema.name suffix) to graph identities; several matches mean the subject was ambiguous and all
    /// are returned for the caller to disambiguate.</summary>
    public static IReadOnlyList<string> ResolveSubject(LineageReport report, string subject)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var flow = report.Flows.FirstOrDefault(f => string.Equals(f.Name, subject, StringComparison.OrdinalIgnoreCase));
        if (flow is not null)
        {
            return ["flow:" + flow.Name];
        }

        var exact = report.Objects.FirstOrDefault(o => string.Equals(o.Key, subject, StringComparison.Ordinal));
        if (exact is not null)
        {
            return [exact.Key];
        }

        // Suffix match on name / schema.name / database.schema.name, case-insensitive.
        var parts = subject.Split('.', StringSplitOptions.TrimEntries);
        var matches = report.Objects.Where(o => parts.Length switch
        {
            1 => string.Equals(o.Name, parts[0], StringComparison.OrdinalIgnoreCase),
            2 => string.Equals(o.Name, parts[1], StringComparison.OrdinalIgnoreCase)
                 && string.Equals(o.Schema, parts[0], StringComparison.OrdinalIgnoreCase),
            3 => string.Equals(o.Name, parts[2], StringComparison.OrdinalIgnoreCase)
                 && string.Equals(o.Schema, parts[1], StringComparison.OrdinalIgnoreCase)
                 && string.Equals(o.Database, parts[0], StringComparison.OrdinalIgnoreCase),
            _ => false,
        }).Select(o => o.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        return matches;
    }

    private static IReadOnlyList<string> Traverse(LineageReport report, string subject, bool downstream)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Adjacency in data-flow direction over flows ("flow:<name>"), objects (node keys), and modules.
        var adjacency = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string from, string to)
        {
            if (!adjacency.TryGetValue(from, out var targets))
            {
                adjacency[from] = targets = new SortedSet<string>(StringComparer.Ordinal);
            }

            targets.Add(to);
        }

        foreach (var edge in report.Edges)
        {
            if (edge.Flow is { } flow)
            {
                var flowId = "flow:" + flow;
                switch (edge.Relation)
                {
                    case LineageRelation.Writes or LineageRelation.Creates:
                        Add(flowId, edge.ObjectKey);
                        break;
                    case LineageRelation.Reads or LineageRelation.Requires:
                        Add(edge.ObjectKey, flowId);
                        break;
                }
            }
            else if (edge.ViaModule is { } module && edge.Relation is LineageRelation.Reads or LineageRelation.Requires)
            {
                // The module reads its base: data flows base -> module.
                Add(edge.ObjectKey, module);
            }
            else if (edge.ViaModule is { } writingModule && edge.Relation is LineageRelation.Writes)
            {
                Add(writingModule, edge.ObjectKey);
            }
        }

        if (!downstream)
        {
            var reversed = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var (from, targets) in adjacency)
            {
                foreach (var to in targets)
                {
                    if (!reversed.TryGetValue(to, out var sources))
                    {
                        reversed[to] = sources = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    sources.Add(from);
                }
            }

            adjacency = reversed;
        }

        string? start;
        if (subject.StartsWith("flow:", StringComparison.Ordinal))
        {
            start = subject;
        }
        else
        {
            var resolved = ResolveSubject(report, subject);
            start = resolved.Count > 0 ? resolved[0] : null;
        }

        if (start is null)
        {
            return [];
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var reached = new List<string>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var target in next)
            {
                if (visited.Add(target))
                {
                    reached.Add(target);
                    queue.Enqueue(target);
                }
            }
        }

        reached.Sort(StringComparer.Ordinal);
        return reached;
    }
}
