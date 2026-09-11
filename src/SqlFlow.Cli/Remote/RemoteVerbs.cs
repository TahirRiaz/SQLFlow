using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Cli.Remote;

/// <summary>
/// The control-plane verb family: everything the GUI can do against <c>/api/v1</c>, driven from the terminal
/// so the core logic is testable without a browser. Local execution verbs (validate/plan/run/...) are
/// untouched; these verbs are thin clients over the same HTTP surface the GUI uses. The target resolves from
/// <c>--url</c> then <c>SQLFLOW_URL</c>; the credential from <c>--token</c>, then <c>SQLFLOW_TOKEN</c> (both
/// suppliable through the git-ignored .sqlflow/env file), then the per-URL store <c>sqlflow login</c> writes.
/// Human output goes to stdout with notes on stderr; <c>--json</c> switches stdout to the raw API shapes so
/// scripts and tests can assert on them. Exit codes: 0 success, 1 error or a followed run that did not
/// succeed, 130 on Ctrl+C.
/// </summary>
internal static partial class RemoteVerbs
{
    /// <summary>True when a control-plane URL is configured (flag or environment), which routes shared verbs
    /// (runs cancel) to the remote path instead of the direct-catalog fallback.</summary>
    public static bool IsConfigured(string[] args)
        => Program.GetOption(args, "--url") is not null
           || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQLFLOW_URL"));

    // ---- health -------------------------------------------------------------------------------------------

    /// <summary>'sqlflow health': probes the control plane's liveness and readiness endpoints (anonymous).
    /// Exit 0 when both answer 200, 1 otherwise.</summary>
    public static async Task<int> HealthAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = new ControlPlaneClient(url);
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            var (liveStatus, liveBody) = await client.ProbeHealthAsync("/health/live", ct).ConfigureAwait(false);
            var (readyStatus, readyBody) = await client.ProbeHealthAsync("/health/ready", ct).ConfigureAwait(false);
            Console.WriteLine($"control plane: {url}");
            Console.WriteLine($"  live:  {(int)liveStatus} {liveBody}");
            Console.WriteLine($"  ready: {(int)readyStatus} {readyBody}");
            var healthy = liveStatus == HttpStatusCode.OK && readyStatus == HttpStatusCode.OK;
            if (!healthy && readyStatus != HttpStatusCode.OK)
            {
                Console.Error.WriteLine("NOTE  'ready' probes the catalog database; a failing ready with a passing live usually means the catalog is unreachable from the control plane.");
            }

            return healthy ? 0 : 1;
        }).ConfigureAwait(false);
    }

    // ---- login / logout -----------------------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow login --url &lt;u&gt;': signs in and stores a personal access token for the URL. Three
    /// credential paths: a local username/password (<c>--username</c>, password from a hidden prompt or piped
    /// stdin), a pasted PAT (<c>--with-token</c>), or the RFC 8628 device grant for SSO users
    /// (<c>--device</c>). Password and device sign-ins yield a short-lived session JWT which is immediately
    /// used to mint a PAT (name <c>--token-name</c>, expiry <c>--expires-days</c>, default 90, or
    /// <c>--no-expiry</c>); the PAT is what gets stored, never the password and never the expiring JWT, so the
    /// stored credential is always revocable server-side. <c>--no-store</c> prints the minted secret once
    /// instead of writing the file (the CI bootstrap path). <c>--scopes "read operate"</c> narrows the token
    /// below the account's own scopes.
    /// </summary>
    public static async Task<int> LoginAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = new ControlPlaneClient(url);
        using var cancel = InterceptCtrlC();
        return await GuardedAsync(url, cancel.Token, async ct =>
        {
            if (args.Contains("--with-token"))
            {
                return await LoginWithPastedTokenAsync(client, url, args, ct).ConfigureAwait(false);
            }

            string subject;
            string grantedScopes;
            if (args.Contains("--device"))
            {
                var granted = await SignInWithDeviceGrantAsync(client, args, ct).ConfigureAwait(false);
                if (granted is null)
                {
                    return 1; // the poll loop already explained the denial/expiry
                }

                client.BearerToken = granted.AccessToken;
                subject = "(device grant)";
                grantedScopes = granted.Scope;
            }
            else
            {
                var username = Program.GetOption(args, "--username");
                if (username is null && !Console.IsInputRedirected)
                {
                    Console.Write("Username: ");
                    username = Console.ReadLine();
                }

                if (string.IsNullOrWhiteSpace(username))
                {
                    Console.Error.WriteLine("ERROR  a username is required: pass --username <name> (the password is read from a hidden prompt or piped stdin, never a flag).");
                    return 1;
                }

                var password = CliConsole.ReadSecret("Password: ");
                if (password.Length == 0)
                {
                    Console.Error.WriteLine("ERROR  an empty password was read; nothing was sent.");
                    return 1;
                }

                var session = await client.LoginAsync(username.Trim(), password, ct).ConfigureAwait(false);
                client.BearerToken = session.AccessToken;
                subject = session.Subject;
                grantedScopes = string.Join(' ', session.Scopes);
            }

            // The session JWT expires within the hour; what persists is a PAT minted through it. Its scopes
            // are capped server-side to the account's own, so --scopes can only narrow.
            var name = Program.GetOption(args, "--token-name") ?? $"cli@{Environment.MachineName}";
            var expiresInDays = args.Contains("--no-expiry") ? (int?)null : Program.ParseIntOption(args, 90, "--expires-days");
            var created = await client.CreateAccessTokenAsync(name, ParseScopes(args), expiresInDays, ct).ConfigureAwait(false);

            Console.WriteLine($"OK   signed in to {url} as {subject}; scopes: {grantedScopes}.");
            if (args.Contains("--no-store"))
            {
                Console.WriteLine($"token '{created.Token.Name}' ({string.Join(' ', created.Token.Scopes)}; {DescribeExpiry(created.Token.ExpiresUtc)}):");
                Console.WriteLine(created.Secret);
                Console.Error.WriteLine("NOTE  the secret above is shown exactly once; set it as SQLFLOW_TOKEN (e.g. in the git-ignored .sqlflow/env).");
                return 0;
            }

            CredentialStore.Save(url, new StoredCredential(
                created.Secret, created.Token.Id, subject, DateTime.UtcNow, created.Token.ExpiresUtc));
            Console.WriteLine(
                $"     token '{created.Token.Name}' ({string.Join(' ', created.Token.Scopes)}; {DescribeExpiry(created.Token.ExpiresUtc)}) stored in {CredentialStore.FilePath}.");
            return 0;
        }).ConfigureAwait(false);
    }

    /// <summary>'sqlflow logout --url &lt;u&gt;': revokes the stored token server-side (when its id is known)
    /// and removes the local entry. Idempotent: nothing stored is a note, not an error.</summary>
    public static async Task<int> LogoutAsync(string[] args)
    {
        var url = RequireUrl(args);
        var stored = CredentialStore.Load(url);
        if (stored is null)
        {
            Console.WriteLine($"NOTE  no stored credential for {url}; nothing to do.");
            return 0;
        }

        if (stored.TokenId is { } tokenId)
        {
            using var client = new ControlPlaneClient(url) { BearerToken = stored.Token };
            try
            {
                var revoked = await client.RevokeAccessTokenAsync(tokenId, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine(revoked
                    ? $"OK   token {tokenId} revoked on {url}."
                    : $"NOTE  the server no longer knows token {tokenId} (already revoked or expired).");
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
            {
                // A token the server already rejects (revoked elsewhere, expired) cannot revoke itself, and an
                // unreachable server must not leave the stale secret on disk; the local removal still happens.
                Console.Error.WriteLine($"WARN  could not revoke the token server-side ({SecretHygiene.RedactedMessage(ex)}); removing it locally anyway. Revoke it from the GUI's token page if it is still active.");
            }
        }
        else
        {
            Console.Error.WriteLine("NOTE  the stored token was pasted without a known server-side id; revoke it from the GUI's token page if it is still active.");
        }

        CredentialStore.Remove(url);
        Console.WriteLine($"OK   credential for {url} removed from {CredentialStore.FilePath}.");
        return 0;
    }

    // ---- trigger ------------------------------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow trigger --repo &lt;name|id&gt; --flow &lt;f&gt;': enqueues a run on the fleet through
    /// <c>POST /api/v1/runs</c>, exactly as the GUI's trigger dialog does. <c>--scope flow|node</c>
    /// picks a single flow (default) or the flow plus its lineage descendants. There is no ad-hoc batch
    /// scope: a whole source runs through its schedule ('sqlflow schedules run &lt;id&gt;'), whose member set
    /// is the single authority on what a source executes. <c>--preview</c> shows what a node scope would
    /// enqueue without enqueuing. The built-in backfill flags are the same as a local run: <c>--full</c>,
    /// <c>--from</c>/<c>--to</c>, <c>--file-pattern</c>; <c>--pool</c> routes to a node pool and
    /// <c>--commit</c> pins an exact git version. <c>--follow</c> attaches to the live trace (single run) or
    /// the member stream (group) and exits by the terminal outcome.
    /// </summary>
    public static async Task<int> TriggerAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        using var cancel = InterceptCtrlC();
        var json = args.Contains("--json");
        return await GuardedAsync(url, cancel.Token, async ct =>
        {
            var repo = await ResolveRepoAsync(client, args, ct).ConfigureAwait(false);
            var flowName = Program.GetOption(args, "--flow");
            var scope = (Program.GetOption(args, "--scope") ?? "flow").Trim().ToLowerInvariant();
            if (scope is "batch")
            {
                Console.Error.WriteLine(
                    "ERROR  --scope batch is gone: a whole source runs through its schedule, whose membership is "
                    + "what a fire runs. Use 'sqlflow schedules list --repo <r>' to find it and 'sqlflow schedules "
                    + "run <id>' to fire it.");
                return 1;
            }

            if (scope is not ("flow" or "node"))
            {
                Console.Error.WriteLine($"ERROR  --scope '{scope}' is not one of flow, node.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(flowName))
            {
                Console.Error.WriteLine($"ERROR  --scope {scope} requires --flow <name>.");
                return 1;
            }

            if (args.Contains("--preview"))
            {
                var preview = await client.PreviewScopeAsync(repo.Id, flowName, scope, batch: null, includeAll: args.Contains("--include-all"), ct).ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(preview, ControlPlaneClient.JsonIndented));
                    return 0;
                }

                Console.WriteLine($"{preview.Scope} '{preview.Anchor}': {preview.MemberCount} flow(s) across {preview.WaveCount} wave(s)");
                foreach (var member in preview.Members)
                {
                    Console.WriteLine($"  wave {member.Wave,3}  {member.FlowKind,-5} {member.FlowName}");
                }

                return 0;
            }

            var parameters = Program.ParseRunParameters(args);
            var request = new RunTriggerRequest(
                repo.Id, flowName,
                Pool: Program.GetOption(args, "--pool"),
                CommitSha: Program.GetOption(args, "--commit"),
                FullLoad: parameters.FullLoad,
                BackfillFrom: parameters.BackfillFrom,
                BackfillTo: parameters.BackfillTo,
                FilePattern: parameters.FilePattern,
                Scope: scope,
                AssertionsOnly: parameters.AssertionsOnly,
                SourceFilter: parameters.SourceFilter,
                // Node scope's "find all": include mode: manual and mode: disabled descendants in the group.
                IncludeAll: args.Contains("--include-all"));
            var outcome = await client.TriggerRunAsync(request, ct).ConfigureAwait(false);

            if (outcome.Run is { } run)
            {
                if (json && !args.Contains("--follow"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(run, ControlPlaneClient.JsonIndented));
                    return 0;
                }

                Note(json, $"OK   run {run.RunId} {run.Status} for '{flowName}' in [{repo.Name}].");
                if (!args.Contains("--follow"))
                {
                    Note(json, $"     follow it: sqlflow runs trace {run.RunId} --follow");
                    return 0;
                }

                return await FollowRunAsync(client, run.RunId, json, ct).ConfigureAwait(false);
            }

            var group = outcome.Group!;
            if (json && !args.Contains("--follow"))
            {
                Console.WriteLine(JsonSerializer.Serialize(group, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Note(json, $"OK   group {group.GroupId} {group.Status}: {group.MemberCount} member flow(s) in [{repo.Name}].");
            if (!args.Contains("--follow"))
            {
                Note(json, $"     follow it: sqlflow groups show {group.GroupId} --follow");
                return 0;
            }

            return await FollowGroupAsync(client, group.GroupId, json, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    // ---- runs ---------------------------------------------------------------------------------------------

    /// <summary>'sqlflow runs list|show|trace|cancel': the run inbox, detail, consolidated trace (with
    /// <c>--follow</c> live streaming), and lifecycle-honoring cancellation, through the control plane.</summary>
    public static async Task<int> RunsAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub is not ("list" or "show" or "trace" or "cancel"))
        {
            Console.Error.WriteLine("ERROR  'runs' supports: list, show <runId>, trace <runId> [--follow], cancel <runId>.");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        using var cancel = InterceptCtrlC();
        var json = args.Contains("--json");
        return await GuardedAsync(url, cancel.Token, async ct =>
        {
            switch (sub)
            {
                case "list":
                {
                    Guid? repoId = null;
                    if (Program.GetOption(args, "--repo") is { } repoArg)
                    {
                        repoId = (await ResolveRepoAsync(client, args, ct).ConfigureAwait(false)).Id;
                    }

                    Guid? groupId = null;
                    if (Program.GetOption(args, "--group") is { } groupArg)
                    {
                        if (!Guid.TryParse(groupArg, out var parsedGroup))
                        {
                            Console.Error.WriteLine($"ERROR  --group '{groupArg}' is not a group id (a GUID).");
                            return 1;
                        }

                        groupId = parsedGroup;
                    }

                    var page = Program.ParseIntOption(args, 1, "--page");
                    var pageSize = Program.ParseIntOption(args, 50, "--page-size");
                    var runs = await client.ListRunsAsync(
                        repoId, Program.GetOption(args, "--status"), Program.GetOption(args, "--flow"),
                        Program.GetOption(args, "--batch"), Program.GetOption(args, "--kind"), groupId,
                        args.Contains("--latest"), page, pageSize, ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(runs, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"{"RUN",-36}  {"STATUS",-9}  {"FLOW",-32}  {"KIND",-5}  {"WAVE",4}  {"DURATION",9}  {"ROWS",10}  WRITTEN (UTC)");
                    foreach (var run in runs.Items)
                    {
                        Console.WriteLine(
                            $"{run.RunId,-36}  {run.Status,-9}  {Truncate(run.FlowName, 32),-32}  {run.FlowKind,-5}  {run.Wave,4}  " +
                            $"{FormatDuration(run.DurationSeconds),9}  {FormatCount(run.RowsLoaded),10}  {run.WrittenUtc:yyyy-MM-dd HH:mm:ss}");
                    }

                    Console.WriteLine($"({runs.Items.Count} of {runs.Total} run(s), page {runs.Page})");
                    return 0;
                }

                case "show":
                {
                    if (!TryRequireId(positional, "runs show", out var runId))
                    {
                        return 1;
                    }

                    var run = await client.GetRunAsync(runId, ct).ConfigureAwait(false);
                    if (run is null)
                    {
                        Console.Error.WriteLine($"ERROR  no run '{runId}'.");
                        return 1;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(run, ControlPlaneClient.JsonIndented));
                    }
                    else
                    {
                        PrintRunDetail(run);
                    }

                    return await PrintRunSectionsAsync(client, runId, args, json, ct).ConfigureAwait(false) ? 0 : 1;
                }

                case "trace":
                {
                    if (!TryRequireId(positional, "runs trace", out var runId))
                    {
                        return 1;
                    }

                    if (args.Contains("--follow"))
                    {
                        return await FollowRunAsync(client, runId, json, ct).ConfigureAwait(false);
                    }

                    if (json)
                    {
                        var trace = await client.GetRunSectionAsync<RunTraceEntryDto>(runId, "trace", 1, 200, ct).ConfigureAwait(false);
                        Console.WriteLine(JsonSerializer.Serialize(trace, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.Write(await client.GetRunTraceTextAsync(runId, ct).ConfigureAwait(false));
                    return 0;
                }

                default: // cancel
                {
                    if (!TryRequireId(positional, "runs cancel", out var runId))
                    {
                        return 1;
                    }

                    return PrintCancelOutcome(await client.CancelRunAsync(runId, ct).ConfigureAwait(false), "run", runId);
                }
            }
        }).ConfigureAwait(false);
    }

    // ---- groups -------------------------------------------------------------------------------------------

    /// <summary>'sqlflow groups show|cancel|rerun &lt;groupId&gt;': a node/batch run group's rollup and members
    /// (with <c>--follow</c> live member streaming), group cancellation, and a fresh re-expansion of the same
    /// anchor (rerun re-resolves the members from current lineage, exactly like the GUI's re-run).</summary>
    public static async Task<int> GroupsAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        if (sub is not ("show" or "cancel" or "rerun"))
        {
            Console.Error.WriteLine("ERROR  'groups' supports: show <groupId> [--follow], cancel <groupId>, rerun <groupId> [--follow].");
            return 1;
        }

        if (!TryRequireId(positional, $"groups {sub}", out var groupId))
        {
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        using var cancel = InterceptCtrlC();
        var json = args.Contains("--json");
        return await GuardedAsync(url, cancel.Token, async ct =>
        {
            switch (sub)
            {
                case "show":
                {
                    var group = await client.GetRunGroupAsync(groupId, ct).ConfigureAwait(false);
                    if (group is null)
                    {
                        Console.Error.WriteLine($"ERROR  no run group '{groupId}'.");
                        return 1;
                    }

                    var members = await client.ListRunsAsync(
                        null, null, null, null, null, groupId, latest: false, page: 1, pageSize: 200, ct).ConfigureAwait(false);
                    if (json && !args.Contains("--follow"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            new { group, members = members.Items }, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Note(json, $"group {group.GroupId}: {group.Mode} '{group.Anchor}', {group.MemberCount} member(s), enqueued {group.EnqueuedUtc:yyyy-MM-dd HH:mm:ss} UTC" +
                               (group.CommitSha is null ? string.Empty : $", commit {group.CommitSha}"));
                    Note(json, $"  {FormatCounts(group.Counts)}");
                    if (!json)
                    {
                        foreach (var member in members.Items)
                        {
                            Console.WriteLine(
                                $"  wave {member.Wave,3}  {member.Status,-9}  {Truncate(member.FlowName, 40),-40}  {FormatDuration(member.DurationSeconds),9}  {member.LastAction ?? string.Empty}");
                        }
                    }

                    if (!args.Contains("--follow"))
                    {
                        return 0;
                    }

                    return await FollowGroupAsync(client, groupId, json, ct).ConfigureAwait(false);
                }

                case "cancel":
                    return PrintCancelOutcome(await client.CancelGroupAsync(groupId, ct).ConfigureAwait(false), "group", groupId);

                default: // rerun
                {
                    var group = await client.GetRunGroupAsync(groupId, ct).ConfigureAwait(false);
                    if (group is null)
                    {
                        Console.Error.WriteLine($"ERROR  no run group '{groupId}'.");
                        return 1;
                    }

                    // A batch-mode group is a schedule fire (the anchor is the schedule's name), and a schedule's
                    // membership is the single authority on what its source runs, so the rerun goes back through
                    // the schedule itself rather than re-deriving the set here.
                    if (string.Equals(group.Mode, "batch", StringComparison.OrdinalIgnoreCase))
                    {
                        return await RerunScheduleGroupAsync(client, group, json, args, ct).ConfigureAwait(false);
                    }

                    // A fresh node expansion of the same anchor flow: the members resolve from the repo's current
                    // synced state (no commit pin), matching the GUI's re-run semantics.
                    var request = new RunTriggerRequest(
                        group.RepoId,
                        group.Anchor,
                        Pool: Program.GetOption(args, "--pool"),
                        Scope: "node");
                    var outcome = await client.TriggerRunAsync(request, ct).ConfigureAwait(false);
                    var rerun = outcome.Group ?? throw new SqlFlowException(
                        $"the control plane accepted the rerun of '{group.Anchor}' as a single run, not a group; check the group's scope.");
                    if (json && !args.Contains("--follow"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(rerun, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Note(json, $"OK   group {rerun.GroupId} {rerun.Status}: {rerun.MemberCount} member flow(s) ({group.Mode} '{group.Anchor}' re-expanded).");
                    if (!args.Contains("--follow"))
                    {
                        return 0;
                    }

                    return await FollowGroupAsync(client, rerun.GroupId, json, ct).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Reruns a schedule-fired (batch-mode) group by firing its schedule again. The group's anchor is the
    /// schedule's name, and a schedule's membership is the single authority on what its source runs, so re-firing
    /// the schedule is the only faithful re-expansion; re-deriving the set here would be a second opinion.</summary>
    private static async Task<int> RerunScheduleGroupAsync(
        ControlPlaneClient client, RunGroupDto group, bool json, string[] args, CancellationToken ct)
    {
        // Resolve the schedule by name within the group's repo, paging until found or exhausted.
        ScheduleDto? schedule = null;
        for (var page = 1; schedule is null; page++)
        {
            var schedules = await client.ListSchedulesAsync(group.RepoId, null, null, page, 50, ct).ConfigureAwait(false);
            schedule = schedules.Items.FirstOrDefault(
                s => string.Equals(s.Name, group.Anchor, StringComparison.OrdinalIgnoreCase));
            if (schedules.Items.Count < 50)
            {
                break;
            }
        }

        if (schedule is null)
        {
            Console.Error.WriteLine(
                $"ERROR  group {group.GroupId} was fired by schedule '{group.Anchor}', which no longer exists in "
                + "this repo; there is nothing to re-expand. Fire the source's current schedule instead "
                + "('sqlflow schedules list --repo <r>').");
            return 1;
        }

        var fired = await client.RunScheduleNowAsync(schedule.Id, ct).ConfigureAwait(false);
        if (fired is null)
        {
            Console.Error.WriteLine($"ERROR  schedule '{schedule.Name}' ({schedule.Id}) disappeared before it could fire.");
            return 1;
        }

        if (json && !args.Contains("--follow"))
        {
            Console.WriteLine(JsonSerializer.Serialize(fired, ControlPlaneClient.JsonIndented));
            return 0;
        }

        Note(json, fired.GroupId is { } firedGroup
            ? $"OK   schedule '{schedule.Name}' re-fired: {fired.MemberCount} member flow(s) as group {firedGroup}."
            : $"OK   schedule '{schedule.Name}' re-fired: run {fired.RunId}.");
        if (!args.Contains("--follow"))
        {
            return 0;
        }

        return fired.GroupId is { } follow
            ? await FollowGroupAsync(client, follow, json, ct).ConfigureAwait(false)
            : await FollowRunAsync(client, fired.RunId, json, ct).ConfigureAwait(false);
    }

    // ---- follow (SSE) -------------------------------------------------------------------------------------

    /// <summary>
    /// Attaches to a run's live consolidated trace and prints each entry as it happens, exactly what the GUI's
    /// Trace tab streams. Reconnects with the server's resume cursors on a transport drop, so a control-plane
    /// restart mid-run loses nothing. Returns the exit code of the terminal outcome: 0 succeeded, 1 anything
    /// else, with the run's summary line printed from the authoritative detail record.
    /// </summary>
    private static async Task<int> FollowRunAsync(ControlPlaneClient client, Guid runId, bool json, CancellationToken ct)
    {
        long afterEventId = 0;
        long afterStatementId = 0;
        string? terminalStatus = null;
        while (terminalStatus is null)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/api/v1/runs/{runId}/trace/stream?afterEventId={afterEventId.ToString(CultureInfo.InvariantCulture)}" +
                       $"&afterStatementId={afterStatementId.ToString(CultureInfo.InvariantCulture)}";
            try
            {
                await foreach (var sse in client.StreamAsync(path, ct).ConfigureAwait(false))
                {
                    if (sse.Name == "entry")
                    {
                        var entry = ControlPlaneClient.ParseEvent<RunTraceEntryDto>(sse);
                        if (entry.Kind == "statement")
                        {
                            afterStatementId = Math.Max(afterStatementId, entry.Id);
                        }
                        else
                        {
                            afterEventId = Math.Max(afterEventId, entry.Id);
                        }

                        if (!json)
                        {
                            PrintTraceEntry(entry);
                        }
                    }
                    else if (sse.Name == "end")
                    {
                        terminalStatus = ControlPlaneClient.ParseEvent<RunTraceStreamEndDto>(sse).Status;
                        break;
                    }
                }

                if (terminalStatus is null)
                {
                    // The server closed the stream without its end event (a restart, a proxy idle-timeout):
                    // reconnect from the cursors after a short breather.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Console.Error.WriteLine($"WARN  live trace dropped ({SecretHygiene.RedactedMessage(ex)}); reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        var detail = await client.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            // The stream reported terminal but the detail vanished (a catalog cleanup between the two calls);
            // the streamed status is still the truthful outcome.
            Note(json, $"run {runId}: {terminalStatus}.");
            return string.Equals(terminalStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(detail, ControlPlaneClient.JsonIndented));
        }
        else
        {
            Console.WriteLine();
            PrintRunDetail(detail);
        }

        return detail.Success ? 0 : 1;
    }

    /// <summary>Attaches to a run group's live member stream, printing each member's status transitions, and
    /// returns 0 only when the terminal rollup has no failed and no cancelled member.</summary>
    private static async Task<int> FollowGroupAsync(ControlPlaneClient client, Guid groupId, bool json, CancellationToken ct)
    {
        var lastPrinted = new Dictionary<Guid, string>();
        RunGroupCountsDto? finalCounts = null;
        while (finalCounts is null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await foreach (var sse in client.StreamAsync($"/api/v1/runs/groups/{groupId}/stream", ct).ConfigureAwait(false))
                {
                    if (sse.Name == "member")
                    {
                        var member = ControlPlaneClient.ParseEvent<RunSummaryDto>(sse);
                        // A reconnect replays the full snapshot; print only genuine changes.
                        var line = $"{member.Status}|{member.LastAction}";
                        if (lastPrinted.TryGetValue(member.RunId, out var previous) && previous == line)
                        {
                            continue;
                        }

                        lastPrinted[member.RunId] = line;
                        if (!json)
                        {
                            Console.WriteLine(
                                $"{DateTime.Now:HH:mm:ss}  {member.Status,-9}  wave {member.Wave,3}  {Truncate(member.FlowName, 40),-40}  {member.LastAction ?? string.Empty}");
                        }
                    }
                    else if (sse.Name == "end")
                    {
                        finalCounts = ControlPlaneClient.ParseEvent<RunGroupCountsDto>(sse);
                        break;
                    }
                }

                if (finalCounts is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Console.Error.WriteLine($"WARN  live group stream dropped ({SecretHygiene.RedactedMessage(ex)}); reconnecting.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(finalCounts, ControlPlaneClient.JsonIndented));
        }
        else
        {
            Console.WriteLine($"group {groupId} finished: {FormatCounts(finalCounts)}");
        }

        return finalCounts.Failed == 0 && finalCounts.Cancelled == 0 ? 0 : 1;
    }

    // ---- login helpers ------------------------------------------------------------------------------------

    private static async Task<int> LoginWithPastedTokenAsync(ControlPlaneClient client, Uri url, string[] args, CancellationToken ct)
    {
        var token = CliConsole.ReadSecret("Personal access token: ").Trim();
        if (token.Length == 0)
        {
            Console.Error.WriteLine("ERROR  an empty token was read; nothing was stored.");
            return 1;
        }

        client.BearerToken = token;
        // The cheapest authenticated call both validates the token and, because a PAT lists its owner's own
        // tokens, lets us find the pasted token's server-side id (by its display prefix) so logout can revoke it.
        var tokens = await client.ListAccessTokensAsync(ct).ConfigureAwait(false);
        var self = tokens.FirstOrDefault(t => t.RevokedUtc is null && token.StartsWith(t.Prefix, StringComparison.Ordinal));

        if (args.Contains("--no-store"))
        {
            Console.WriteLine($"OK   the token authenticates against {url}{(self is null ? string.Empty : $" (token '{self.Name}', scopes {string.Join(' ', self.Scopes)})")}.");
            return 0;
        }

        CredentialStore.Save(url, new StoredCredential(token, self?.Id, null, DateTime.UtcNow, self?.ExpiresUtc));
        Console.WriteLine($"OK   token{(self is null ? string.Empty : $" '{self.Name}'")} stored for {url} in {CredentialStore.FilePath}.");
        if (self is null)
        {
            Console.Error.WriteLine("NOTE  the token is valid but is not one of this account's own tokens (a bootstrap credential?); logout will only remove it locally.");
        }

        return 0;
    }

    private static async Task<DeviceTokenResponse?> SignInWithDeviceGrantAsync(ControlPlaneClient client, string[] args, CancellationToken ct)
    {
        var scope = Program.GetOption(args, "--scopes") ?? "read operate";
        var authorization = await client.StartDeviceAuthorizationAsync(scope, ct).ConfigureAwait(false);
        Console.WriteLine($"To sign in, open   {authorization.VerificationUriComplete}");
        Console.WriteLine($"and confirm code   {authorization.UserCode}");
        Console.WriteLine($"Waiting for approval (expires in {authorization.ExpiresIn}s; Ctrl+C aborts)...");

        var interval = TimeSpan.FromSeconds(Math.Max(1, authorization.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(authorization.ExpiresIn);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            var (token, error) = await client.PollDeviceTokenAsync(authorization.DeviceCode, ct).ConfigureAwait(false);
            if (token is not null)
            {
                return token;
            }

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    Console.Error.WriteLine("ERROR  the sign-in was denied in the browser.");
                    return null;
                case "expired_token":
                    Console.Error.WriteLine("ERROR  the device code expired before approval; run 'sqlflow login --device' again.");
                    return null;
                default:
                    throw new SqlFlowException($"the device sign-in failed: {error ?? "(no error code)"}.");
            }
        }

        Console.Error.WriteLine("ERROR  the device code expired before approval; run 'sqlflow login --device' again.");
        return null;
    }

    private static IReadOnlyList<string>? ParseScopes(string[] args)
    {
        var raw = Program.GetOption(args, "--scopes");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string DescribeExpiry(DateTime? expiresUtc)
        => expiresUtc is { } expiry ? $"expires {expiry:yyyy-MM-dd}" : "never expires";

    // ---- shared plumbing ----------------------------------------------------------------------------------

    /// <summary>The control-plane URL from <c>--url</c> or <c>SQLFLOW_URL</c>; absolute http(s) only, so a
    /// mistyped host fails here with guidance instead of as a DNS error three calls later.</summary>
    internal static Uri RequireUrl(string[] args)
    {
        var raw = Program.GetOption(args, "--url") ?? Environment.GetEnvironmentVariable("SQLFLOW_URL");
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new SqlFlowException(
                "no control plane is configured: pass --url <https://host> or set SQLFLOW_URL (e.g. in the git-ignored .sqlflow/env file).");
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new SqlFlowException($"the control plane URL '{raw}' is not an absolute http(s) URL (e.g. https://sqlflow.example.com).");
        }

        return url;
    }

    /// <summary>The bearer credential for a control-plane URL: <c>--token</c>, then <c>SQLFLOW_TOKEN</c>, then the
    /// credential <c>sqlflow login</c> stored for that URL; null when none is configured.</summary>
    internal static string? ResolveToken(Uri url, string[] args)
        => Program.GetOption(args, "--token")
           ?? Environment.GetEnvironmentVariable("SQLFLOW_TOKEN")
           ?? CredentialStore.Load(url)?.Token;

    /// <summary>A client carrying the resolved bearer credential: <c>--token</c>, then <c>SQLFLOW_TOKEN</c>,
    /// then the stored credential for the URL. No credential is not an error here; the server's 401 (decorated
    /// with sign-in guidance) is the single authoritative rejection path.</summary>
    private static ControlPlaneClient CreateAuthenticatedClient(Uri url, string[] args)
    {
        var token = ResolveToken(url, args);
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine($"NOTE  no credential for {url} (no --token, no SQLFLOW_TOKEN, nothing stored); the request will be anonymous.");
        }

        return new ControlPlaneClient(url) { BearerToken = token };
    }

    /// <summary>Runs a verb body, converting transport failures (unreachable host, TLS, timeout) into the
    /// CLI's one-line error convention, and Ctrl+C into the conventional 130.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification = "Private CLI helper: the token precedes the body delegate so every call site reads url -> token -> lambda; the delegate is required, not an optional trailing argument.")]
    private static async Task<int> GuardedAsync(Uri url, CancellationToken ct, Func<CancellationToken, Task<int>> body)
    {
        try
        {
            return await body(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"ERROR  cannot reach the control plane at {url}: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("CANCELLED  interrupted (Ctrl+C).");
            return 130;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR  the request to {url} timed out.");
            return 1;
        }
    }

    /// <summary>Wires Ctrl+C to a cancellation token instead of process death, so an interrupted follow or
    /// poll exits with a clean 130 and the terminal is not left mid-line.</summary>
    private static CancellationTokenSource InterceptCtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    /// <summary>Resolves <c>--repo</c> (a GUID or a repo name) against the catalog's repo list.</summary>
    private static async Task<RepoDto> ResolveRepoAsync(ControlPlaneClient client, string[] args, CancellationToken ct)
    {
        var raw = Program.GetOption(args, "--repo")
                  ?? throw new SqlFlowException("--repo <name|id> is required (the repo the flow was synced from).");
        var repos = await client.ListReposAsync(ct).ConfigureAwait(false);
        if (Guid.TryParse(raw, out var id))
        {
            return repos.FirstOrDefault(r => r.Id == id)
                   ?? throw new SqlFlowException($"no repo with id '{id}' in the catalog.");
        }

        var byName = repos.Where(r => string.Equals(r.Name, raw, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new SqlFlowException(
                $"no repo named '{raw}' in the catalog. Known repos: {string.Join(", ", repos.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(15))}."),
            _ => throw new SqlFlowException($"'{raw}' matches {byName.Count} repos; use the id ({string.Join(", ", byName.Select(r => r.Id))})."),
        };
    }

    private static bool TryRequireId(string[] positional, string verb, out Guid id)
    {
        if (positional.Length < 3 || !Guid.TryParse(positional[2], out id))
        {
            Console.Error.WriteLine($"ERROR  '{verb}' requires an id: sqlflow {verb} <guid>.");
            id = Guid.Empty;
            return false;
        }

        return true;
    }

    private static int PrintCancelOutcome(RemoteCancelOutcome outcome, string what, Guid id)
    {
        switch (outcome)
        {
            case RemoteCancelOutcome.Cancelled:
                Console.WriteLine($"OK   {what} {id} was queued and is now cancelled.");
                return 0;
            case RemoteCancelOutcome.Cancelling:
                Console.WriteLine($"OK   {what} {id} is running; cancellation requested. Its node will abort the in-flight statement and record it cancelled.");
                return 0;
            case RemoteCancelOutcome.NotFound:
                Console.Error.WriteLine($"ERROR  no {what} '{id}'.");
                return 1;
            default:
                Console.Error.WriteLine($"ERROR  {what} '{id}' has already finished and cannot be cancelled.");
                return 1;
        }
    }

    /// <summary>Fetches and prints the drill-down sections requested by flags on 'runs show'. Returns false
    /// only when a requested section's fetch failed in a way that did not throw (none today), so the exit code
    /// path stays honest if one is added.</summary>
    private static async Task<bool> PrintRunSectionsAsync(ControlPlaneClient client, Guid runId, string[] args, bool json, CancellationToken ct)
    {
        if (args.Contains("--files"))
        {
            var files = await client.GetRunSectionAsync<RunFileDto>(runId, "files", 1, 200, ct).ConfigureAwait(false);
            PrintSection(json, "files", files, f => $"  {f.Name}  {f.Rows} row(s), {f.Columns} column(s), {f.SizeBytes} byte(s){(f.Path is null ? string.Empty : $"  {f.Path}")}");
        }

        if (args.Contains("--statements"))
        {
            var statements = await client.GetRunSectionAsync<RunStatementDto>(runId, "statements", 1, 200, ct).ConfigureAwait(false);
            PrintSection(json, "statements", statements, s =>
                $"  [{s.Ordinal}] {s.Step}{(s.Error is null ? string.Empty : $"  ERROR {s.Error}")}\n{Indent(s.Sql, "      ")}");
        }

        if (args.Contains("--assertions"))
        {
            var assertions = await client.GetRunSectionAsync<RunAssertionDto>(runId, "assertions", 1, 200, ct).ConfigureAwait(false);
            PrintSection(json, "assertions", assertions, a =>
                $"  {a.Name}: {(a.Error is not null ? $"error: {a.Error}" : a.Evaluated ? $"result: {a.Result} (asserted {a.AssertedValue})" : "skipped")}");
        }

        if (args.Contains("--keys"))
        {
            var keys = await client.GetRunSectionAsync<RunSurrogateKeyDto>(runId, "surrogate-keys", 1, 200, ct).ConfigureAwait(false);
            PrintSection(json, "surrogate keys", keys, k =>
                $"  {k.SurrogateTable}.{k.SurrogateColumn}: {(k.Error is not null ? $"error: {k.Error}" : $"{k.KeysGenerated} key(s) generated, {k.RowsStamped} row(s) stamped")}");
        }

        if (args.Contains("--metrics"))
        {
            var metrics = await client.GetRunSectionAsync<RunHealthCheckMetricDto>(runId, "health-metrics", 1, 200, ct).ConfigureAwait(false);
            PrintSection(json, "health metrics", metrics, m =>
                $"  {m.Name}: {m.Anomalies} anomalies in {m.SeriesPoints} point(s) ({m.ImputedPoints} imputed, {m.ImmaturePoints} immature){(m.Error is null ? string.Empty : $"  ERROR {m.Error}")}");
        }

        return true;
    }

    private static void PrintSection<T>(bool json, string title, PagedResult<T> section, Func<T, string> render)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(section, ControlPlaneClient.JsonIndented));
            return;
        }

        Console.WriteLine($"{title} ({section.Items.Count} of {section.Total}):");
        foreach (var item in section.Items)
        {
            Console.WriteLine(render(item));
        }
    }

    private static void PrintRunDetail(RunDetailDto run)
    {
        Console.WriteLine($"run {run.RunId}: {run.Status}");
        Console.WriteLine($"  flow:        {run.FlowName} ({run.FlowKind}), batch {run.Batch}, wave {run.Wave}");
        Console.WriteLine($"  repo:        {run.RepoId?.ToString() ?? "(none)"}  pipeline {run.PipelineId}{(run.GroupId is null ? string.Empty : $"  group {run.GroupId}")}");
        Console.WriteLine($"  lifecycle:   enqueued {FormatUtc(run.EnqueuedUtc)}, start {FormatUtc(run.StartUtc)}, end {FormatUtc(run.EndUtc)}, duration {FormatDuration(run.DurationSeconds)}");
        Console.WriteLine($"  executed by: {run.ClaimedByNode ?? run.Host ?? "(unknown)"}{(run.TargetPool is null ? string.Empty : $" (pool {run.TargetPool})")}{(run.CommitSha is null ? string.Empty : $", commit {run.CommitSha}")}");
        if (run.RowsLoaded is not null || run.RowsInserted is not null || run.RowsUpdated is not null || run.RowsDeleted is not null || run.FileCount > 0)
        {
            Console.WriteLine($"  rows:        {FormatCount(run.RowsLoaded)} loaded, {FormatCount(run.RowsInserted)} inserted, {FormatCount(run.RowsUpdated)} updated, {FormatCount(run.RowsDeleted)} deleted, {run.FileCount} file(s)");
        }

        if (run.AssertionsOnly)
        {
            Console.WriteLine("  parameters:  assertions only (evaluated against the current target; nothing was loaded)");
        }

        if (run.FullLoad || run.BackfillFrom is not null || run.BackfillTo is not null || run.FilePattern is not null)
        {
            Console.WriteLine($"  backfill:    {(run.FullLoad ? "full load; " : string.Empty)}window {FormatUtc(run.BackfillFrom)} -> {FormatUtc(run.BackfillTo)}{(run.FilePattern is null ? string.Empty : $"; pattern {run.FilePattern}")}");
        }

        if (run.ReprocessFromSourceMin)
        {
            Console.WriteLine("  backfill:    reprocess from source min (reads MIN from source instead of MAX from target)");
        }

        if (run.IncrementalMode is not null)
        {
            Console.WriteLine($"  incremental: {run.IncrementalMode}{(run.IncrementalWatermark is null ? string.Empty : $", watermark {run.IncrementalWatermark} ({run.IncrementalWatermarkSource})")}{(run.IncrementalFilter is null ? string.Empty : $", filter {run.IncrementalFilter}")}");
        }

        if (run.DataSetConvention is not null)
        {
            Console.WriteLine($"  dataset:     {run.DataSetConvention}");
        }

        if (run.Error is not null)
        {
            Console.WriteLine($"  error:       {run.Error}");
        }

        if (run.FailedStatementSql is not null)
        {
            Console.WriteLine($"  failed at:   statement {run.FailedStatementOrdinal} ({run.FailedStatementStep})");
            Console.WriteLine(Indent(run.FailedStatementSql, "      "));
        }
    }

    private static void PrintTraceEntry(RunTraceEntryDto entry)
    {
        var stamp = entry.TimestampUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";
        if (entry.Kind == "statement")
        {
            Console.WriteLine($"{stamp}  sql      {entry.Step ?? string.Empty}{(entry.Error is null ? string.Empty : $"  ERROR {entry.Error}")}");
            if (entry.Sql is { Length: > 0 } sql)
            {
                Console.WriteLine(Indent(sql, "          "));
            }

            return;
        }

        var extras = new StringBuilder();
        if (entry.Rows is { } rows)
        {
            extras.Append("  [").Append(rows.ToString("N0", CultureInfo.InvariantCulture)).Append(" row(s)");
            if (entry.ElapsedMs is { } elapsedWithRows)
            {
                extras.Append(", ").Append(elapsedWithRows.ToString("0", CultureInfo.InvariantCulture)).Append(" ms");
            }

            extras.Append(']');
        }
        else if (entry.ElapsedMs is { } elapsed)
        {
            extras.Append("  [").Append(elapsed.ToString("0", CultureInfo.InvariantCulture)).Append(" ms]");
        }

        Console.WriteLine($"{stamp}  {entry.Level,-7}  {(entry.Step is null ? string.Empty : entry.Step + "  ")}{entry.Message}{extras}");
    }

    /// <summary>Writes a human note: stdout normally, stderr when --json owns stdout.</summary>
    private static void Note(bool json, string message)
    {
        if (json)
        {
            Console.Error.WriteLine(message);
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    private static string FormatCounts(RunGroupCountsDto counts)
        => $"{counts.Total} total: {counts.Queued} queued, {counts.Running} running, {counts.Succeeded} succeeded, {counts.Failed} failed, {counts.Cancelled} cancelled, {counts.Skipped} skipped";

    private static string FormatUtc(DateTime? value)
        => value is { } utc ? utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "-";

    private static string FormatDuration(double? seconds)
        => seconds is { } s
            ? s >= 3600 ? TimeSpan.FromSeconds(s).ToString(@"h\h\ m\m", CultureInfo.InvariantCulture)
            : s >= 60 ? TimeSpan.FromSeconds(s).ToString(@"m\m\ s\s", CultureInfo.InvariantCulture)
            : s.ToString("0.#", CultureInfo.InvariantCulture) + "s"
            : "-";

    private static string FormatCount(long? value)
        => value is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : "-";

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Indent(string text, string prefix)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join('\n', lines.Select(line => prefix + line));
    }
}
