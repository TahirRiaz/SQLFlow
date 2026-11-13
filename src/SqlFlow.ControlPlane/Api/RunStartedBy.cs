using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Api;

/// <summary>The kinds of <see cref="RunStartedByDto.Kind"/>: what started a run, as a reader asks it.</summary>
public static class RunStartedByKinds
{
    /// <summary>A schedule fired on its own, on its clock (and every schedule fire recorded before a chained one was
    /// told apart).</summary>
    public const string Schedule = "schedule";

    /// <summary>A schedule fired on its own behind its parents' fire.</summary>
    public const string Chain = "chain";

    /// <summary>A person or an API client asked: the GUI's Run button, a schedule's Run now, an API call, an MCP tool,
    /// a module's operator action.</summary>
    public const string User = "user";

    /// <summary>An external trigger asked: a GitHub workflow, or a system holding a trigger key.</summary>
    public const string Trigger = "trigger";

    /// <summary>Run from the command line, and recorded from its run file.</summary>
    public const string Cli = "cli";

    /// <summary>Recorded before runs kept what started them.</summary>
    public const string Unknown = "unknown";

    /// <summary>Every kind, in the order the runs board offers them as a filter.</summary>
    public static readonly IReadOnlyList<string> All = [Schedule, Chain, User, Trigger, Cli, Unknown];
}

/// <summary>
/// What started a run, in the one shape every client reads (the GUI, the CLI, the MCP server): <see cref="Kind"/> (one of
/// <see cref="RunStartedByKinds"/>), <see cref="Summary"/> saying it in one sentence, and <see cref="Who"/> asked when a
/// person, a client, an external trigger or a command-line account did. <see cref="ScheduleId"/> and
/// <see cref="ScheduleName"/> name the schedule whose member set the run is one of, whether it fired on its own or was
/// asked to. For a chained fire, <see cref="AfterRunId"/> is the first run of the parent fire it followed, from
/// <see cref="AfterFlowName"/>'s schedule <see cref="AfterScheduleName"/>. <see cref="TriggerRequestId"/> is the external
/// trigger's request that asked for the run.
/// </summary>
public sealed record RunStartedByDto(
    string Kind, string Summary, string? Who, Guid? ScheduleId, string? ScheduleName,
    Guid? AfterRunId, string? AfterScheduleName, string? AfterFlowName, Guid? TriggerRequestId);

/// <summary>One link of the chain behind a chained run, from the parent fire it followed back to the first link: the
/// link's run (the first run of that fire), its flow, its group, its status, and what started it.</summary>
public sealed record RunCauseDto(Guid RunId, string FlowName, Guid? GroupId, string Status, RunStartedByDto StartedBy);

/// <summary>
/// Says what started a run, from what the run recorded when it was enqueued (<see cref="CatalogRun.TriggerSource"/>,
/// <see cref="CatalogRun.RequestedBy"/>, <see cref="CatalogRun.TriggerScheduleId"/>, <see cref="CatalogRun.TriggerRunId"/>)
/// and the names those link to. The one place the answer is worked out and worded, so the runs board, a run's page, a
/// run group, the CLI and the MCP server all say the same thing.
/// </summary>
public static class RunStartedBy
{
    /// <summary>The longest chain of parent fires a run's read walks back; a chain longer than this is cut there.</summary>
    public const int MaxCauses = 16;

    /// <summary>What a run recorded about how it started, with the names its links resolve to.</summary>
    /// <param name="TriggerSource">The run's trigger source (<see cref="RunTriggerSources"/>), or null when not recorded.</param>
    /// <param name="RequestedBy">Who the run records as the one that asked, or null.</param>
    /// <param name="ScheduleId">The schedule whose member set the run is one of.</param>
    /// <param name="ScheduleName">That schedule's name, when its row is still in the catalog.</param>
    /// <param name="AfterRunId">The parent fire's run a chained fire followed.</param>
    /// <param name="AfterScheduleName">The schedule of that parent fire, when known.</param>
    /// <param name="AfterFlowName">The flow of that parent fire's run, when known.</param>
    /// <param name="TriggerRequestId">The external trigger's request that asked for the run.</param>
    public sealed record Facts(
        string? TriggerSource, string? RequestedBy, Guid? ScheduleId, string? ScheduleName,
        Guid? AfterRunId, string? AfterScheduleName, string? AfterFlowName, Guid? TriggerRequestId);

    /// <summary>
    /// What started a run, from <paramref name="facts"/>. Who asked, when the run names someone, decides over the trigger
    /// source: a run that names a person or an external trigger was asked for, whatever source an older control plane
    /// recorded it with, and one that names its schedule (<c>schedule:&lt;name&gt;</c>) was the schedule's own fire.
    /// </summary>
    public static RunStartedByDto Describe(Facts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var requestedBy = string.IsNullOrWhiteSpace(facts.RequestedBy) ? null : facts.RequestedBy.Trim();
        var scheduleName = facts.ScheduleName
            ?? (requestedBy is not null && requestedBy.StartsWith(RunActors.SchedulePrefix, StringComparison.Ordinal)
                ? requestedBy[RunActors.SchedulePrefix.Length..]
                : null);

        if (facts.TriggerSource == RunTriggerSources.Chain)
        {
            var after = facts.AfterScheduleName is null ? "its parent schedules" : $"schedule {facts.AfterScheduleName}";
            return Make(RunStartedByKinds.Chain, $"{ScheduleSubject(scheduleName)} fired after {after} finished.", null);
        }

        if (facts.TriggerSource == RunTriggerSources.Cli)
        {
            var account = Without(requestedBy, RunActors.CliPrefix);
            return Make(
                RunStartedByKinds.Cli,
                account is null ? "Run from the command line, and recorded from its run file." : $"Run from the command line by {account}.",
                account);
        }

        if (requestedBy is not null && requestedBy.StartsWith(RunActors.TriggerPrefix, StringComparison.Ordinal))
        {
            var trigger = Without(requestedBy, RunActors.TriggerPrefix) ?? requestedBy;
            return Make(
                RunStartedByKinds.Trigger,
                scheduleName is null
                    ? $"External trigger {trigger} asked for this run."
                    : $"External trigger {trigger} fired schedule {scheduleName}.",
                trigger);
        }

        if (requestedBy is null || requestedBy.StartsWith(RunActors.SchedulePrefix, StringComparison.Ordinal))
        {
            return facts.TriggerSource is null && requestedBy is null
                ? Make(RunStartedByKinds.Unknown, "Not recorded: the run predates the record of what starts a run.", null)
                : facts.TriggerSource == RunTriggerSources.Manual && requestedBy is null
                    ? Make(RunStartedByKinds.User, AskedSummary(null, scheduleName), null)
                    : Make(RunStartedByKinds.Schedule, $"{ScheduleSubject(scheduleName)} fired on its own.", null);
        }

        var who = requestedBy == RequestActor.Unknown ? null : requestedBy;
        return Make(RunStartedByKinds.User, AskedSummary(who, scheduleName), who);

        RunStartedByDto Make(string kind, string summary, string? who) => new(
            kind, summary, who, facts.ScheduleId, scheduleName, facts.AfterRunId, facts.AfterScheduleName,
            facts.AfterFlowName, facts.TriggerRequestId);
    }

    /// <summary>
    /// Narrows <paramref name="runs"/> to those <see cref="Describe"/> gives the kind <paramref name="kind"/>, by the same
    /// rules in SQL, so the runs board's "Started by" filter and the label each row shows always agree. Null for a kind
    /// not in <see cref="RunStartedByKinds.All"/>.
    /// </summary>
    internal static IQueryable<CatalogRun>? Filter(IQueryable<CatalogRun> runs, string kind)
    {
        // The actor prefixes as LIKE patterns: neither holds a wildcard character, so each matches itself.
        var trigger = RunActors.TriggerPrefix + "%";
        var schedule = RunActors.SchedulePrefix + "%";
        var notChainOrCli = runs.Where(r => r.TriggerSource != RunTriggerSources.Chain && r.TriggerSource != RunTriggerSources.Cli);
        return kind switch
        {
            RunStartedByKinds.Chain => runs.Where(r => r.TriggerSource == RunTriggerSources.Chain),
            RunStartedByKinds.Cli => runs.Where(r => r.TriggerSource == RunTriggerSources.Cli),
            RunStartedByKinds.Trigger => notChainOrCli.Where(r => r.RequestedBy != null && EF.Functions.Like(r.RequestedBy, trigger)),
            RunStartedByKinds.Schedule => notChainOrCli.Where(r =>
                (r.RequestedBy != null && EF.Functions.Like(r.RequestedBy, schedule))
                || (r.RequestedBy == null && r.TriggerSource != null && r.TriggerSource != RunTriggerSources.Manual)),
            RunStartedByKinds.User => notChainOrCli.Where(r =>
                (r.RequestedBy != null && !EF.Functions.Like(r.RequestedBy, trigger) && !EF.Functions.Like(r.RequestedBy, schedule))
                || (r.RequestedBy == null && r.TriggerSource == RunTriggerSources.Manual)),
            RunStartedByKinds.Unknown => runs.Where(r => r.TriggerSource == null && r.RequestedBy == null),
            _ => null,
        };
    }

    /// <summary>The rows a summary needs to say what started it: its id and what it recorded.</summary>
    internal sealed record Recorded(
        Guid RunId, string? TriggerSource, string? RequestedBy, Guid? ScheduleId, Guid? AfterRunId, Guid? FanOutRoot);

    /// <summary>
    /// What started each of <paramref name="runs"/>, read in at most three bounded queries whatever their number: the
    /// parent fire runs the chained ones followed, the names of every schedule involved, and the external trigger
    /// requests behind the runs a trigger asked for (a fan-out member is matched through its root, which the request
    /// recorded).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, RunStartedByDto>> ReadAsync(
        CatalogDbContext db, IReadOnlyCollection<Recorded> runs, CancellationToken ct)
    {
        if (runs.Count == 0)
        {
            return new Dictionary<Guid, RunStartedByDto>();
        }

        var afterIds = runs.Where(r => r.AfterRunId is not null).Select(r => r.AfterRunId!.Value).Distinct().ToList();
        var afterRuns = afterIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking()
                .Where(r => afterIds.Contains(r.RunId))
                .Select(r => new { r.RunId, r.FlowName, r.TriggerScheduleId })
                .ToListAsync(ct).ConfigureAwait(false);
        var afterById = afterRuns.ToDictionary(r => r.RunId);

        var scheduleIds = runs.Select(r => r.ScheduleId)
            .Concat(afterRuns.Select(r => r.TriggerScheduleId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var scheduleNames = scheduleIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Schedules.AsNoTracking()
                .Where(s => scheduleIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct).ConfigureAwait(false);

        var askedByTrigger = runs
            .Where(r => r.RequestedBy is not null && r.RequestedBy.StartsWith(RunActors.TriggerPrefix, StringComparison.Ordinal))
            .ToList();
        var requestRunIds = askedByTrigger.Select(r => r.FanOutRoot ?? r.RunId).Distinct().ToList();
        var requestByRun = requestRunIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : (await db.TriggerRequestRuns.AsNoTracking()
                    .Where(t => requestRunIds.Contains(t.RunId))
                    .Select(t => new { t.RunId, t.RequestId })
                    .ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(t => t.RunId)
                .ToDictionary(g => g.Key, g => g.First().RequestId);

        var answer = new Dictionary<Guid, RunStartedByDto>(runs.Count);
        foreach (var run in runs)
        {
            var after = run.AfterRunId is { } afterId ? afterById.GetValueOrDefault(afterId) : null;
            answer[run.RunId] = Describe(new Facts(
                run.TriggerSource, run.RequestedBy, run.ScheduleId, NameOf(run.ScheduleId), run.AfterRunId,
                NameOf(after?.TriggerScheduleId), after?.FlowName,
                requestByRun.TryGetValue(run.FanOutRoot ?? run.RunId, out var request) ? request : null));
        }

        return answer;

        string? NameOf(Guid? scheduleId) => scheduleId is { } id ? scheduleNames.GetValueOrDefault(id) : null;
    }

    /// <summary>
    /// The chain of parent fires behind a run, from the fire it followed back to the first link, at most
    /// <see cref="MaxCauses"/> long: each link is the first run of a fire, with what started it. Empty for a run no
    /// parent fire set off. A link whose run has left the catalog ends the chain there.
    /// </summary>
    internal static async Task<IReadOnlyList<RunCauseDto>> ReadCausesAsync(
        CatalogDbContext db, Guid? afterRunId, CancellationToken ct)
    {
        var causes = new List<RunCauseDto>();
        var seen = new HashSet<Guid>();
        var next = afterRunId;
        while (next is { } runId && causes.Count < MaxCauses && seen.Add(runId))
        {
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.RunId == runId)
                .Select(r => new
                {
                    r.RunId, r.FlowName, r.GroupId, r.Status, r.TriggerSource, r.RequestedBy, r.TriggerScheduleId,
                    r.TriggerRunId, r.FanOutRoot,
                })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (run is null)
            {
                break;
            }

            var startedBy = await ReadAsync(
                db,
                [new Recorded(run.RunId, run.TriggerSource, run.RequestedBy, run.TriggerScheduleId, run.TriggerRunId, run.FanOutRoot)],
                ct).ConfigureAwait(false);
            causes.Add(new RunCauseDto(run.RunId, run.FlowName, run.GroupId, run.Status, startedBy[run.RunId]));
            next = run.TriggerRunId;
        }

        return causes;
    }

    private static string AskedSummary(string? who, string? scheduleName)
        => (who, scheduleName) switch
        {
            (null, null) => "Asked for by a caller whose sign-in carried no name.",
            (null, _) => $"Schedule {scheduleName} was run now by a caller whose sign-in carried no name.",
            (_, null) => $"{who} asked for this run.",
            _ => $"{who} ran schedule {scheduleName} now.",
        };

    private static string ScheduleSubject(string? scheduleName)
        => scheduleName is null ? "A schedule" : $"Schedule {scheduleName}";

    private static string? Without(string? label, string prefix)
    {
        if (label is null || !label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = label[prefix.Length..].Trim();
        return rest.Length == 0 ? null : rest;
    }
}
