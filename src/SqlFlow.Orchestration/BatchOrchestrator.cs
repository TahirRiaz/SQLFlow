using System.Diagnostics;
using Microsoft.Extensions.FileSystemGlobbing;
using SqlFlow.Core.Batch;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage;

namespace SqlFlow.Orchestration;

/// <summary>
/// Runs a batch: lineage computes the dependency graph over the selected member flows, then members are dispatched
/// as a DAG: each member starts as soon as every one of its direct dependencies has completed (bounded by
/// maxParallel), so a member never waits on an unrelated slow member. The topological waves are still computed and
/// reported (they are how a plan reads, and each member records its wave), but they are levels, not execution
/// barriers. Failure is explicit: <c>onError: stop</c> lets in-flight members finish and skips every member not
/// yet started; <c>onError: continue</c> keeps running independent members and skips only those that depend on a
/// failure; a member listed under <c>ignoreErrors</c> may fail without stopping the batch or blocking its
/// dependents. Members in a dependency cycle run together in a final fallback wave, after every acyclic member has
/// finished, and are reported as unordered. Every member runs through the shared <see cref="IDocumentRunner"/>,
/// the same path as a directly-invoked flow.
/// </summary>
public sealed class BatchOrchestrator
{
    private readonly IDocumentRunner _runner;

    public BatchOrchestrator(IDocumentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
    }

    public async Task<BatchRunResult> RunAsync(
        BatchFlow flow,
        string batchFile,
        ISecretResolver secrets,
        DocumentExecutionOptions memberOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchFile);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(memberOptions);

        var runId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var flowDirectory = Path.GetDirectoryName(Path.GetFullPath(batchFile)) ?? Directory.GetCurrentDirectory();

        // ---- Lineage, then membership; auto-connect a second pass when an sp member needs module-derived order.
        var connect = flow.Connect == BatchConnectMode.Always;
        var report = await ComputeLineageAsync(flowDirectory, connect, secrets, ct).ConfigureAwait(false);
        var members = ResolveMembers(report, flow);

        if (flow.Connect == BatchConnectMode.Auto && !connect && members.Any(m => string.Equals(m.Kind, "sp", StringComparison.OrdinalIgnoreCase)))
        {
            report = await ComputeLineageAsync(flowDirectory, connect: true, secrets, ct).ConfigureAwait(false);
            members = ResolveMembers(report, flow);
        }

        if (members.Count == 0)
        {
            return Failed(runId, flow, stopwatch,
                $"no member flows matched include {Quote(flow.Include)} under '{flowDirectory}'.");
        }

        // A name declared by several documents is collapsed to one flow node by lineage (first wins) so the
        // other declarations never become distinct members; silently running one and skipping the rest is
        // dangerous in an orchestrated batch. Lineage exposes the collision in a structured form, so a member
        // whose name is duplicated across files hard-fails the batch with the colliding files named.
        var duplicate = report.DuplicateFlowNames
            .FirstOrDefault(d => members.Any(m => string.Equals(m.Name, d.Name, StringComparison.OrdinalIgnoreCase)));
        if (duplicate is not null)
        {
            return Failed(runId, flow, stopwatch,
                $"member flow name '{duplicate.Name}' is declared by {duplicate.Files.Count} files " +
                $"({string.Join(", ", duplicate.Files)}); names must be unique within a batch.");
        }

        var active = members.Where(m => !m.Inactive && !m.Manual).ToList();
        var (waves, unordered) = ComputeMemberWaves(active.Select(m => m.Name).ToList(), report.FlowDependencies);
        if (unordered.Count > 0)
        {
            warnings.Add($"dependency cycle among members ({string.Join(", ", unordered)}); they run together in the final wave, order undecidable.");
        }

        // ---- DAG execution: a member starts when its own direct dependencies complete. ----------------------
        var byName = active.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var directDeps = DirectMemberDependencies(active.Select(m => m.Name).ToList(), report.FlowDependencies);
        // With no explicit cap, bound by the machine rather than launching an arbitrarily wide estate at once.
        var maxParallel = flow.MaxParallel <= 0 ? Math.Max(2, Environment.ProcessorCount) : flow.MaxParallel;
        using var gate = new SemaphoreSlim(maxParallel);

        var memberResults = new Dictionary<string, BatchMemberResult>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // failed (non-ignored) or skipped
        var stopRequested = false;

        // The waves are reported as computed (each member also records its wave), but execution below is
        // per-member: a wave describes the plan's topological levels, never a barrier.
        var waveResults = new List<BatchWaveResult>();
        var waveOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var w = 0; w < waves.Count; w++)
        {
            waveResults.Add(new BatchWaveResult { Wave = w + 1, Members = waves[w] });
            foreach (var name in waves[w])
            {
                waveOf[name] = w + 1;
            }
        }

        // Only the acyclic members are dispatched here; cycle members run in the final fallback phase below, so
        // their (undrainable) dependency counts are excluded. A dependent of a cycle member is itself unordered
        // (Kahn never reaches it), so the acyclic subgraph is self-contained and every count here does drain.
        var cycleMembers = new HashSet<string>(unordered, StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var remainingDeps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in active)
        {
            if (!cycleMembers.Contains(member.Name))
            {
                dependents[member.Name] = [];
                remainingDeps[member.Name] = directDeps[member.Name].Count;
            }
        }

        foreach (var name in remainingDeps.Keys)
        {
            foreach (var dep in directDeps[name])
            {
                dependents[dep].Add(name);
            }
        }

        // A member becomes ready when its last direct dependency completes. At dequeue time it is either skipped
        // (the batch stopped, or a dependency failed or was skipped; both recorded with the reason) or started,
        // still bounded by the maxParallel gate. Skips complete the member too, so their dependents cascade.
        var ready = new Queue<string>(remainingDeps.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var running = new HashSet<Task<(Member Member, BatchMemberResult Result)>>();
        while (ready.Count > 0 || running.Count > 0)
        {
            while (ready.Count > 0)
            {
                var name = ready.Dequeue();
                var blockingDep = directDeps[name].FirstOrDefault(blocked.Contains);
                if (stopRequested || blockingDep is not null)
                {
                    blocked.Add(name);
                    memberResults[name] = Skipped(byName[name], waveOf[name],
                        stopRequested ? "batch stopped before this member started" : $"depends on '{blockingDep}', which did not succeed");
                    ReleaseDependents(name, dependents, remainingDeps, ready);
                    continue;
                }

                running.Add(RunMemberAsync(byName[name], waveOf[name], flowDirectory, memberOptions, gate, ct));
            }

            if (running.Count == 0)
            {
                break;
            }

            var finishedTask = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(finishedTask);
            Member member;
            BatchMemberResult result;
            try
            {
                (member, result) = await finishedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The batch is being cancelled. The sibling member tasks observe the same token; wait for them to
                // unwind so no member task outlives the batch, then let the cancellation reach the caller.
                await Task.WhenAll(running.Select(AwaitCancelledMemberAsync)).ConfigureAwait(false);
                throw;
            }

            memberResults[member.Name] = result;
            if (result.Status == BatchMemberStatus.Failed)
            {
                blocked.Add(member.Name);
                if (flow.OnError == BatchErrorMode.Stop)
                {
                    stopRequested = true;
                }
            }

            ReleaseDependents(member.Name, dependents, remainingDeps, ready);
        }

        // ---- Cycle members: the reported fallback wave, run only after every acyclic member finished. --------
        if (unordered.Count > 0)
        {
            var toRun = new List<string>();
            foreach (var name in waves[^1])
            {
                var blockingDep = directDeps[name].FirstOrDefault(blocked.Contains);
                if (stopRequested || blockingDep is not null)
                {
                    blocked.Add(name);
                    memberResults[name] = Skipped(byName[name], waveOf[name],
                        stopRequested ? "batch stopped before this wave" : $"depends on '{blockingDep}', which did not succeed");
                    continue;
                }

                toRun.Add(name);
            }

            if (toRun.Count > 0)
            {
                var outcomes = await Task.WhenAll(toRun.Select(name => RunMemberAsync(byName[name], waveOf[name], flowDirectory, memberOptions, gate, ct)))
                    .ConfigureAwait(false);
                foreach (var (member, result) in outcomes)
                {
                    memberResults[member.Name] = result;
                }
            }
        }

        // ---- Inactive members: declared, deactivated this run. ---------------------------------------------
        foreach (var member in members.Where(m => m.Inactive))
        {
            memberResults[member.Name] = new BatchMemberResult
            {
                FlowName = member.Name,
                FlowKind = member.Kind,
                File = member.File,
                Wave = 0,
                Status = BatchMemberStatus.Inactive,
            };
        }

        // ---- Manual members: their own document opted out of automatic execution (mode: manual), so the
        //      batch reports them without running them; they execute only when triggered directly. An
        //      inactive declaration wins (deactivated is stronger than deferred).
        foreach (var member in members.Where(m => m.Manual && !m.Inactive))
        {
            memberResults[member.Name] = new BatchMemberResult
            {
                FlowName = member.Name,
                FlowKind = member.Kind,
                File = member.File,
                Wave = 0,
                Status = BatchMemberStatus.Manual,
            };
        }

        stopwatch.Stop();
        var ordered = memberResults.Values
            .OrderBy(m => m.Wave)
            .ThenBy(m => m.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var failed = ordered.Count(m => m.Status == BatchMemberStatus.Failed);
        var skipped = ordered.Count(m => m.Status == BatchMemberStatus.Skipped);

        return new BatchRunResult
        {
            RunId = runId,
            Success = failed == 0 && skipped == 0,
            BatchName = flow.SysAlias,
            OnError = flow.OnError.ToString(),
            Waves = waveResults,
            Members = ordered,
            Unordered = unordered,
            Warnings = warnings,
            Succeeded = ordered.Count(m => m.Status == BatchMemberStatus.Succeeded),
            Failed = failed,
            Skipped = skipped,
            Inactive = ordered.Count(m => m.Status == BatchMemberStatus.Inactive),
            Manual = ordered.Count(m => m.Status == BatchMemberStatus.Manual),
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
        };
    }

    private async Task<(Member Member, BatchMemberResult Result)> RunMemberAsync(
        Member member, int wave, string flowDirectory, DocumentExecutionOptions options, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = Path.GetFullPath(Path.Combine(flowDirectory, member.File));
            DocumentRunOutcome outcome;
            try
            {
                // The member's name selects which flow of its file executes: a document that expands into more
                // than one pipeline (an ingestion flow with an embedded healthCheck: block) shares one file
                // between the load member and the derived hc member.
                outcome = await _runner.RunAsync(file, options with { FlowName = member.Name }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A runner is expected to return a failed outcome, not throw; guard anyway so one member's
                // unexpected throw cannot abort the batch dispatch.
                outcome = new DocumentRunOutcome
                {
                    FlowName = member.Name,
                    FlowKind = member.Kind,
                    Success = false,
                    Error = SecretHygiene.RedactedMessage(ex),
                };
            }

            var status = outcome.Success
                ? BatchMemberStatus.Succeeded
                : member.Ignorable ? BatchMemberStatus.FailedIgnored : BatchMemberStatus.Failed;

            return (member, new BatchMemberResult
            {
                FlowName = member.Name,
                FlowKind = outcome.FlowKind,
                File = member.File,
                Wave = wave,
                Status = status,
                Error = outcome.Error,
                RunId = outcome.RunId == Guid.Empty ? null : outcome.RunId,
                RunDirectory = outcome.RunDirectory,
                DurationSeconds = outcome.DurationSeconds,
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Marks <paramref name="name"/> complete for dispatch purposes: every dependent's remaining
    /// dependency count drops by one, and a dependent that reaches zero becomes ready (it is skipped or started
    /// when dequeued). Applied on success, failure, and skip alike; whether a dependent then runs is decided by
    /// the blocked set at dequeue time.</summary>
    private static void ReleaseDependents(
        string name, Dictionary<string, List<string>> dependents, Dictionary<string, int> remainingDeps, Queue<string> ready)
    {
        foreach (var dependent in dependents[name])
        {
            if (--remainingDeps[dependent] == 0)
            {
                ready.Enqueue(dependent);
            }
        }
    }

    /// <summary>Awaits a sibling member task during batch cancellation. Cancellation is the only way a member task
    /// faults (<see cref="RunMemberAsync"/> converts every other throw into a failed outcome), and it is swallowed
    /// here because the primary cancellation is what propagates to the caller.</summary>
    private static async Task AwaitCancelledMemberAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during unwind; the first observed cancellation is rethrown by the dispatch loop.
        }
    }

    private static async Task<LineageReport> ComputeLineageAsync(string flowDirectory, bool connect, ISecretResolver secrets, CancellationToken ct)
        => await LineageService.ComputeAsync(new LineageOptions
        {
            FlowDirectory = flowDirectory,
            IncludeObserved = true,
            IncludeDerived = connect,
            Secrets = secrets,
        }, ct).ConfigureAwait(false);

    /// <summary>Selects member flows from the lineage report by the include/exclude globs, marking the inactive
    /// and ignore-error subsets. Matching is over the flows' repository-relative file paths, so a file that is
    /// not a data-moving flow (a batch document, an unparseable file) is never a member.</summary>
    private static List<Member> ResolveMembers(LineageReport report, BatchFlow flow)
    {
        var files = report.Flows.Select(f => f.File).ToList();
        var included = Match(flow.Include, flow.Exclude, files);
        var inactive = Match(flow.Inactive, [], files);
        var ignore = Match(flow.IgnoreErrors, [], files);

        return report.Flows
            .Where(f => included.Contains(f.File))
            .Select(f => new Member(
                f.Name, f.File, f.Kind, inactive.Contains(f.File), ignore.Contains(f.File),
                f.Mode != ExecutionMode.Auto))
            .OrderBy(m => m.File, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> Match(IReadOnlyList<string> include, IReadOnlyList<string> exclude, IReadOnlyList<string> files)
    {
        if (include.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(include);
        if (exclude.Count > 0)
        {
            matcher.AddExcludePatterns(exclude);
        }

        var matched = matcher.Match(files).Files.Select(m => m.Path);
        // The matcher returns its own normalized stems; intersect back with the real file list (case-insensitive)
        // so a returned path always maps to an actual flow file.
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return matched.Where(fileSet.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The direct member-to-member dependency map (a member -> the members it must wait for), filtered
    /// to the active set. Because a member is only dispatched once every direct dependency has completed, and a
    /// skipped dependency joins the blocked set itself, checking only direct dependencies for a blocked upstream
    /// is sufficient to skip transitive dependents.</summary>
    private static Dictionary<string, HashSet<string>> DirectMemberDependencies(
        IReadOnlyList<string> names, IReadOnlyList<LineageFlowDependency> dependencies)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var map = names.ToDictionary(n => n, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var dep in dependencies)
        {
            if (set.Contains(dep.FromFlow) && set.Contains(dep.ToFlow)
                && !string.Equals(dep.FromFlow, dep.ToFlow, StringComparison.OrdinalIgnoreCase))
            {
                map[dep.ToFlow].Add(dep.FromFlow);
            }
        }

        return map;
    }

    /// <summary>Topological waves (the reported plan levels) over the member subgraph: modified Kahn with
    /// max-level assignment (a member's wave is one past its latest dependency). Cycle members fall into a final
    /// wave and are returned as unordered, never refused.</summary>
    private static (List<IReadOnlyList<string>> Waves, List<string> Unordered) ComputeMemberWaves(
        IReadOnlyList<string> names, IReadOnlyList<LineageFlowDependency> dependencies)
    {
        var n = names.Count;
        if (n == 0)
        {
            return ([], []);
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < n; i++)
        {
            index[names[i]] = i;
        }

        var deps = new HashSet<int>[n];
        var forward = new List<int>[n];
        for (var i = 0; i < n; i++)
        {
            deps[i] = [];
            forward[i] = [];
        }

        foreach (var dep in dependencies)
        {
            if (index.TryGetValue(dep.FromFlow, out var from) && index.TryGetValue(dep.ToFlow, out var to) && from != to)
            {
                deps[to].Add(from);
            }
        }

        var inDegree = new int[n];
        for (var i = 0; i < n; i++)
        {
            inDegree[i] = deps[i].Count;
            foreach (var j in deps[i])
            {
                forward[j].Add(i);
            }
        }

        var level = new int[n];
        var processed = new HashSet<int>();
        var queue = new Queue<int>(Enumerable.Range(0, n).Where(i => inDegree[i] == 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            processed.Add(current);
            foreach (var dependent in forward[current])
            {
                level[dependent] = Math.Max(level[dependent], level[current] + 1);
                if (--inDegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        var unordered = new List<string>();
        if (processed.Count < n)
        {
            var fallback = (processed.Count > 0 ? processed.Max(i => level[i]) : -1) + 1;
            foreach (var i in Enumerable.Range(0, n).Where(i => !processed.Contains(i)))
            {
                level[i] = fallback;
                unordered.Add(names[i]);
            }

            unordered.Sort(StringComparer.OrdinalIgnoreCase);
        }

        var waves = Enumerable.Range(0, n)
            .GroupBy(i => level[i])
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)g.Select(i => names[i]).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        return (waves, unordered);
    }

    private static BatchMemberResult Skipped(Member member, int wave, string reason) => new()
    {
        FlowName = member.Name,
        FlowKind = member.Kind,
        File = member.File,
        Wave = wave,
        Status = BatchMemberStatus.Skipped,
        Error = reason,
    };

    private static BatchRunResult Failed(Guid runId, BatchFlow flow, Stopwatch stopwatch, string error)
    {
        stopwatch.Stop();
        return new BatchRunResult
        {
            RunId = runId,
            Success = false,
            Error = error,
            BatchName = flow.SysAlias,
            OnError = flow.OnError.ToString(),
            Waves = [],
            Members = [],
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
        };
    }

    private static string Quote(IReadOnlyList<string> patterns) => "[" + string.Join(", ", patterns) + "]";

    private sealed record Member(string Name, string File, string Kind, bool Inactive, bool Ignorable, bool Manual);
}
