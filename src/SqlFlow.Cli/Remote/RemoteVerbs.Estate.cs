using System.Globalization;
using System.Net;
using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Cli.Remote;

/// <summary>
/// The estate half of the control-plane verb family: everything the GUI's catalog, explore, and operate pages
/// show, as terminal data. The lineage graph cannot be drawn richly in a console, but its DATASET can be
/// printed, and with <c>--json</c> every verb here emits the raw API shapes, which is exactly what an LLM or
/// an integration test wants to consume. Conventions match the run verbs: target from --url/SQLFLOW_URL,
/// credential from --token/SQLFLOW_TOKEN/the store, notes on stderr, exit 0/1 (130 on Ctrl+C).
/// </summary>
internal static partial class RemoteVerbs
{
    // ---- whoami -------------------------------------------------------------------------------------------

    /// <summary>'sqlflow whoami': who the resolved credential authenticates as (subject, role, effective
    /// scopes) and where that credential came from, so "why 403" is answerable in one call.</summary>
    public static async Task<int> WhoAmIAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            var identity = await client.GetIdentityAsync(ct).ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(identity, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine($"{identity.Subject} on {url}");
            Console.WriteLine($"  role:       {identity.Role ?? "(none; a bootstrap token)"}");
            Console.WriteLine($"  scopes:     {string.Join(' ', identity.Scopes)}");
            Console.WriteLine($"  account:    {(identity.UserId is { } id ? id.ToString() : "(no catalog user)")}");
            Console.WriteLine($"  credential: {DescribeTokenSource(args, url)}");
            return 0;
        }).ConfigureAwait(false);
    }

    // ---- summary and nodes --------------------------------------------------------------------------------

    /// <summary>'sqlflow summary': the dashboard rollup as one screenful (or one JSON object).</summary>
    public static async Task<int> SummaryAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            var summary = await client.GetSummaryAsync(ct).ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(summary, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine($"SQLFlow estate on {url} (as of {summary.AsOfUtc:yyyy-MM-dd HH:mm:ss} UTC)");
            Console.WriteLine($"  repos:      {summary.Repos}  pipelines: {summary.ActivePipelines} active of {summary.Pipelines}");
            Console.WriteLine($"  runs:       {summary.Runs.Queued} queued, {summary.Runs.Running} running, {summary.Runs.Succeeded} succeeded, {summary.Runs.Failed} failed, {summary.Runs.Cancelled} cancelled ({summary.Runs.Last24h} in 24h)");
            Console.WriteLine($"  nodes:      {summary.NodesOnline} online of {summary.NodesTotal}");
            Console.WriteLine($"  schedules:  {summary.SchedulesEnabled} enabled, {summary.SchedulesPaused} paused");
            Console.WriteLine($"  git sync:   {summary.RepoSources} source(s), {summary.RepoSourcesWithErrors} with errors");
            return 0;
        }).ConfigureAwait(false);
    }

    /// <summary>'sqlflow nodes': the worker fleet with heartbeat-derived liveness.</summary>
    public static async Task<int> NodesAsync(string[] args)
    {
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            var nodes = await client.ListNodesAsync(
                Program.ParseIntOption(args, 1, "--page"), Program.ParseIntOption(args, 50, "--page-size"), ct).ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(nodes, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine($"{"NODE",-30}  {"STATE",-8}  {"VERSION",-12}  {"FIRST SEEN (UTC)",-19}  LAST SEEN (UTC)");
            foreach (var node in nodes.Items)
            {
                Console.WriteLine(
                    $"{Truncate(node.Name, 30),-30}  {(node.Online ? "online" : "offline"),-8}  {node.Version ?? "-",-12}  " +
                    $"{node.FirstSeenUtc:yyyy-MM-dd HH:mm:ss}  {node.LastSeenUtc:yyyy-MM-dd HH:mm:ss}");
            }

            Console.WriteLine($"({nodes.Items.Count} of {nodes.Total} node(s))");
            return 0;
        }).ConfigureAwait(false);
    }

    // ---- schedules ----------------------------------------------------------------------------------------

    /// <summary>'sqlflow schedules list|show|create|pause|resume|delete': the scheduling surface. 'create'
    /// takes exactly one of --cron or --interval (seconds); --timezone defaults to UTC server-side.</summary>
    public static async Task<int> SchedulesAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : "list";
        if (sub is not ("list" or "show" or "create" or "run" or "pause" or "resume" or "delete"))
        {
            Console.Error.WriteLine("ERROR  'schedules' supports: list, show <id>, create, run <id>, pause <id>, resume <id>, delete <id>.");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            switch (sub)
            {
                case "list":
                {
                    Guid? repoId = Program.GetOption(args, "--repo") is null
                        ? null
                        : (await ResolveRepoAsync(client, args, ct).ConfigureAwait(false)).Id;
                    bool? enabled = Program.GetOption(args, "--enabled") is { } e ? bool.Parse(e) : null;
                    var schedules = await client.ListSchedulesAsync(
                        repoId, null, enabled,
                        Program.ParseIntOption(args, 1, "--page"), Program.ParseIntOption(args, 50, "--page-size"), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(schedules, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"{"SCHEDULE",-36}  {"NAME",-28}  {"MEMBERS",7}  {"TRIGGER",-22}  {"STATE",-8}  NEXT FIRE (UTC)");
                    foreach (var schedule in schedules.Items)
                    {
                        var trigger = schedule.Cron ?? $"every {schedule.IntervalSeconds}s";
                        var state = schedule.Paused ? "paused" : schedule.Enabled ? "enabled" : "disabled";
                        Console.WriteLine(
                            $"{schedule.Id,-36}  {Truncate(schedule.Name, 28),-28}  {schedule.MemberPipelineIds.Count,7}  {Truncate(trigger, 22),-22}  {state,-8}  {FormatUtc(schedule.NextFireUtc)}");
                    }

                    Console.WriteLine($"({schedules.Items.Count} of {schedules.Total} schedule(s))");
                    return 0;
                }

                case "show":
                {
                    if (!TryRequireId(positional, "schedules show", out var id))
                    {
                        return 1;
                    }

                    var schedule = await client.GetScheduleAsync(id, ct).ConfigureAwait(false);
                    if (schedule is null)
                    {
                        Console.Error.WriteLine($"ERROR  no schedule '{id}'.");
                        return 1;
                    }

                    Console.WriteLine(JsonSerializer.Serialize(schedule, ControlPlaneClient.JsonIndented));
                    return 0;
                }

                case "create":
                {
                    // The cheap client-side shape checks come before any catalog round trip, so a wrong
                    // invocation is answered instantly and identically whether or not the repo exists.
                    // A schedule owns a member SET (what a fire runs): --flow takes one name or a
                    // comma-separated list, and every member joins the same wave-ordered fire.
                    var flow = Program.GetOption(args, "--flow");
                    var members = (flow ?? string.Empty)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (members.Length == 0)
                    {
                        Console.Error.WriteLine("ERROR  'schedules create' requires --flow <name[,name...]>: membership is what a fire runs.");
                        return 1;
                    }

                    var cron = Program.GetOption(args, "--cron");
                    var intervalRaw = Program.GetOption(args, "--interval");
                    if ((cron is null) == (intervalRaw is null))
                    {
                        Console.Error.WriteLine("ERROR  'schedules create' takes exactly one of --cron <expr> or --interval <seconds>.");
                        return 1;
                    }

                    int? maxConcurrency = null;
                    if (Program.GetOption(args, "--max-concurrency") is { } widthRaw)
                    {
                        if (!int.TryParse(widthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width <= 0)
                        {
                            Console.Error.WriteLine($"ERROR  --max-concurrency '{widthRaw}' is not a positive number.");
                            return 1;
                        }

                        maxConcurrency = width;
                    }

                    var repo = await ResolveRepoAsync(client, args, ct).ConfigureAwait(false);

                    int? interval = null;
                    if (intervalRaw is not null)
                    {
                        if (!int.TryParse(intervalRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                        {
                            Console.Error.WriteLine($"ERROR  --interval '{intervalRaw}' is not a positive number of seconds.");
                            return 1;
                        }

                        interval = seconds;
                    }

                    var created = await client.CreateScheduleAsync(new CreateScheduleRequest(
                        repo.Id, members, cron, interval, Program.GetOption(args, "--timezone"),
                        Enabled: !args.Contains("--disabled"),
                        Catchup: args.Contains("--catchup") ? true : null,
                        Name: Program.GetOption(args, "--name"),
                        MaxConcurrency: maxConcurrency), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(created, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine(
                        $"OK   schedule {created.Id} created with {members.Length} member flow(s) in [{repo.Name}]; "
                        + $"next fire {FormatUtc(created.NextFireUtc)} UTC.");
                    return 0;
                }

                case "run":
                {
                    if (!TryRequireId(positional, "schedules run", out var id))
                    {
                        return 1;
                    }

                    var fired = await client.RunScheduleNowAsync(id, ct).ConfigureAwait(false);
                    if (fired is null)
                    {
                        Console.Error.WriteLine($"ERROR  no schedule '{id}'.");
                        return 1;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(fired, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine(fired.GroupId is { } firedGroup
                        ? $"OK   schedule {id} fired; enqueued {fired.MemberCount} member flow(s) as group {firedGroup}. Follow it with: sqlflow groups show {firedGroup} --follow"
                        : $"OK   schedule {id} fired; enqueued run {fired.RunId}. Follow it with: sqlflow runs show {fired.RunId} --follow");
                    return 0;
                }

                default: // pause / resume / delete
                {
                    if (!TryRequireId(positional, $"schedules {sub}", out var id))
                    {
                        return 1;
                    }

                    if (sub == "delete")
                    {
                        if (!await client.DeleteScheduleAsync(id, ct).ConfigureAwait(false))
                        {
                            Console.Error.WriteLine($"ERROR  no schedule '{id}'.");
                            return 1;
                        }

                        Console.WriteLine($"OK   schedule {id} deleted.");
                        return 0;
                    }

                    var schedule = sub == "pause"
                        ? await client.PauseScheduleAsync(id, ct).ConfigureAwait(false)
                        : await client.ResumeScheduleAsync(id, ct).ConfigureAwait(false);
                    if (schedule is null)
                    {
                        Console.Error.WriteLine($"ERROR  no schedule '{id}'.");
                        return 1;
                    }

                    Console.WriteLine($"OK   schedule {id} {(schedule.Paused ? "paused" : $"resumed; next fire {FormatUtc(schedule.NextFireUtc)} UTC")}.");
                    return 0;
                }
            }
        }).ConfigureAwait(false);
    }

    // ---- repos --------------------------------------------------------------------------------------------

    /// <summary>'sqlflow repos list|show|register|discover|sync': the synced-repo surface, merging catalog
    /// repos with their managed git sources exactly as the GUI's repos page does. 'sync' picks the right
    /// mechanism per repo: a managed source syncs from git, a local-path repo re-syncs from disk.</summary>
    public static async Task<int> ReposAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : "list";
        if (sub is not ("list" or "show" or "register" or "discover" or "sync"))
        {
            Console.Error.WriteLine("ERROR  'repos' supports: list, show <name|id>, register, discover, sync <name|id>.");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            switch (sub)
            {
                case "list":
                {
                    var repos = await client.ListReposAsync(ct).ConfigureAwait(false);
                    var sources = (await client.ListRepoSourcesAsync(1, 200, ct).ConfigureAwait(false)).Items
                        .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { repos, sources = sources.Values }, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"{"REPO",-28}  {"KIND",-6}  {"LAST SYNC (UTC)",-19}  {"COMMIT",-12}  REMOTE / PATH");
                    foreach (var repo in repos.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var source = sources.GetValueOrDefault(repo.Name);
                        var kind = source is not null ? "git" : repo.RootPath is not null ? "local" : "-";
                        var sha = source?.LastSyncedSha is { Length: >= 8 } s ? s[..8] : "-";
                        Console.WriteLine(
                            $"{Truncate(repo.Name, 28),-28}  {kind,-6}  {repo.LastSyncUtc:yyyy-MM-dd HH:mm:ss}  {sha,-12}  {repo.RemoteUrl ?? repo.RootPath ?? "-"}");
                        if (source?.LastError is { Length: > 0 } error)
                        {
                            Console.WriteLine($"{string.Empty,-28}  sync error: {Truncate(error, 100)}");
                        }
                    }

                    Console.WriteLine($"({repos.Count} repo(s), {sources.Count} managed source(s))");
                    return 0;
                }

                case "show":
                {
                    var repo = await ResolveRepoArgumentAsync(client, positional, "repos show", ct).ConfigureAwait(false);
                    if (repo is null)
                    {
                        return 1;
                    }

                    var source = (await client.ListRepoSourcesAsync(1, 200, ct).ConfigureAwait(false)).Items
                        .FirstOrDefault(s => string.Equals(s.Name, repo.Name, StringComparison.OrdinalIgnoreCase));
                    Console.WriteLine(JsonSerializer.Serialize(new { repo, source }, ControlPlaneClient.JsonIndented));
                    return 0;
                }

                case "register":
                {
                    var name = Program.GetOption(args, "--name");
                    var remote = Program.GetOption(args, "--remote-url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(remote))
                    {
                        Console.Error.WriteLine("ERROR  'repos register' requires --name <repo> and --remote-url <git url>.");
                        return 1;
                    }

                    var credential = Program.GetOption(args, "--credential-ref");
                    if (credential is not null && SecretHygiene.LooksLikeEmbeddedSecret(credential))
                    {
                        Console.Error.WriteLine(
                            "ERROR  --credential-ref must be a secret REFERENCE like ${env:GIT_TOKEN} or ${keyvault:vault/secret}, never the raw token.");
                        return 1;
                    }

                    var registered = await client.RegisterRepoSourceAsync(new RegisterRepoSourceRequest(
                        name, remote, Program.GetOption(args, "--branch"),
                        Program.GetOption(args, "--interval") is { } i ? int.Parse(i, CultureInfo.InvariantCulture) : null,
                        Enabled: !args.Contains("--disabled"),
                        CredentialReference: credential,
                        CredentialUsername: Program.GetOption(args, "--credential-user"),
                        ExcludedFlowPaths: null), ct).ConfigureAwait(false);
                    Note(json, $"OK   source '{name}' registered ({registered.Id}); the control plane syncs it on its interval, or force it: sqlflow repos sync {name}");
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(registered, ControlPlaneClient.JsonIndented));
                    }

                    return 0;
                }

                case "discover":
                {
                    var remote = Program.GetOption(args, "--remote-url");
                    if (string.IsNullOrWhiteSpace(remote))
                    {
                        Console.Error.WriteLine("ERROR  'repos discover' requires --remote-url <git url>.");
                        return 1;
                    }

                    var flows = await client.DiscoverRepoAsync(new DiscoverRepoRequest(
                        remote, Program.GetOption(args, "--branch"),
                        Program.GetOption(args, "--credential-ref"), Program.GetOption(args, "--credential-user")), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(flows, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var flow in flows)
                    {
                        Console.WriteLine(flow.ParseOk
                            ? $"  {flow.Kind ?? "?",-6} {flow.FlowName ?? "(unnamed)",-40} {flow.RelativePath}"
                            : $"  BROKEN {flow.RelativePath}: {flow.ParseError}");
                    }

                    Console.WriteLine($"({flows.Count} flow file(s) in {remote})");
                    return 0;
                }

                default: // sync
                {
                    var repo = await ResolveRepoArgumentAsync(client, positional, "repos sync", ct).ConfigureAwait(false);
                    if (repo is null)
                    {
                        return 1;
                    }

                    var source = (await client.ListRepoSourcesAsync(1, 200, ct).ConfigureAwait(false)).Items
                        .FirstOrDefault(s => string.Equals(s.Name, repo.Name, StringComparison.OrdinalIgnoreCase));
                    if (source is not null)
                    {
                        var synced = await client.SyncRepoSourceAsync(source.Id, ct).ConfigureAwait(false);
                        if (synced is null)
                        {
                            Console.Error.WriteLine($"ERROR  the source for '{repo.Name}' is disabled or gone; nothing was synced.");
                            return 1;
                        }

                        if (json)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(synced, ControlPlaneClient.JsonIndented));
                            return 0;
                        }

                        Console.WriteLine(synced.LastError is { Length: > 0 } error
                            ? $"FAILED  sync of '{repo.Name}': {error}"
                            : $"OK   '{repo.Name}' synced to {synced.LastSyncedSha} at {FormatUtc(synced.LastSyncUtc)} UTC.");
                        return synced.LastError is { Length: > 0 } ? 1 : 0;
                    }

                    var result = await client.SyncLocalRepoAsync(repo.Id, ct).ConfigureAwait(false);
                    if (result is null)
                    {
                        Console.Error.WriteLine($"ERROR  no repo '{repo.Name}'.");
                        return 1;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine(
                        $"OK   synced '{repo.Name}': pipelines +{result.PipelinesAdded} added, {result.PipelinesUpdated} updated, " +
                        $"{result.PipelinesUnchanged} unchanged, {result.PipelinesDeactivated} deactivated, {result.PipelinesDeleted} removed; lineage {result.Objects} objects, " +
                        $"{result.Edges} edges, {result.Waves} waves{(result.Connected ? " (connected)" : string.Empty)}.");
                    foreach (var warning in result.Warnings.Take(20))
                    {
                        Console.Error.WriteLine($"WARN  {warning}");
                    }

                    return 0;
                }
            }
        }).ConfigureAwait(false);
    }

    // ---- pipelines ----------------------------------------------------------------------------------------

    /// <summary>'sqlflow pipelines list|show|columns|files': the pipeline registry. 'show' prints the facts;
    /// --yaml or --definition switch stdout to the raw document (LLM- and pipe-friendly).</summary>
    public static async Task<int> PipelinesAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : "list";
        if (sub is not ("list" or "show" or "columns" or "files"))
        {
            Console.Error.WriteLine("ERROR  'pipelines' supports: list, show <id> [--yaml|--definition], columns <id>, files <id>.");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            if (sub == "list")
            {
                Guid? repoId = Program.GetOption(args, "--repo") is null
                    ? null
                    : (await ResolveRepoAsync(client, args, ct).ConfigureAwait(false)).Id;
                bool? active = Program.GetOption(args, "--active") is { } a ? bool.Parse(a) : null;
                var pipelines = await client.ListPipelinesAsync(
                    repoId, Program.GetOption(args, "--kind"), active, Program.GetOption(args, "--name"),
                    Program.ParseIntOption(args, 1, "--page"), Program.ParseIntOption(args, 50, "--page-size"), ct).ConfigureAwait(false);
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(pipelines, ControlPlaneClient.JsonIndented));
                    return 0;
                }

                Console.WriteLine($"{"PIPELINE",-36}  {"NAME",-36}  {"KIND",-5}  {"BATCH",-16}  {"WAVE",4}  {"ACTIVE",-6}  PATH");
                foreach (var pipeline in pipelines.Items)
                {
                    Console.WriteLine(
                        $"{pipeline.Id,-36}  {Truncate(pipeline.Name, 36),-36}  {pipeline.Kind,-5}  {Truncate(pipeline.Batch ?? "-", 16),-16}  " +
                        $"{pipeline.Wave,4}  {(pipeline.Active ? "yes" : "no"),-6}  {pipeline.RelativePath}");
                }

                Console.WriteLine($"({pipelines.Items.Count} of {pipelines.Total} pipeline(s))");
                return 0;
            }

            if (!TryRequireId(positional, $"pipelines {sub}", out var id))
            {
                return 1;
            }

            switch (sub)
            {
                case "show":
                {
                    var pipeline = await client.GetPipelineAsync(id, ct).ConfigureAwait(false);
                    if (pipeline is null)
                    {
                        Console.Error.WriteLine($"ERROR  no pipeline '{id}'.");
                        return 1;
                    }

                    if (args.Contains("--yaml"))
                    {
                        Console.Write(pipeline.Yaml);
                        return 0;
                    }

                    if (args.Contains("--definition"))
                    {
                        Console.WriteLine(pipeline.DefinitionJson);
                        return 0;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(pipeline, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"pipeline {pipeline.Id}: {pipeline.Name} ({pipeline.Kind}){(pipeline.Active ? string.Empty : "  INACTIVE")}");
                    Console.WriteLine($"  repo:    {pipeline.RepoId}  path {pipeline.RelativePath}");
                    Console.WriteLine($"  batch:   {pipeline.Batch ?? "-"}  wave {pipeline.Wave}  mode {pipeline.ExecutionMode}  lifecycle {pipeline.Lifecycle}");
                    Console.WriteLine($"  servers: {pipeline.SourceServer ?? "-"} -> {pipeline.TargetServer ?? "-"}");
                    Console.WriteLine($"  seen:    {pipeline.FirstSeenUtc:yyyy-MM-dd} .. {pipeline.LastSeenUtc:yyyy-MM-dd}  hash {pipeline.ContentHash[..12]}");
                    Console.WriteLine("  (print the document: --yaml or --definition)");
                    return 0;
                }

                case "columns":
                {
                    var columns = await client.GetPipelineColumnsAsync(id, Program.GetOption(args, "--kind"), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(columns, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var column in columns)
                    {
                        Console.WriteLine(
                            $"  [{column.Ordinal,3}] {column.Kind,-9} {column.ColumnName,-40} {column.DataType ?? "-",-16}" +
                            $"{(column.Expression is null ? string.Empty : $"  = {column.Expression}")}");
                    }

                    Console.WriteLine($"({columns.Count} column(s))");
                    return 0;
                }

                default: // files
                {
                    var files = await client.GetPipelineFilesAsync(
                        id, Program.GetOption(args, "--search"),
                        Program.ParseIntOption(args, 1, "--page"), Program.ParseIntOption(args, 50, "--page-size"), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(files, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var file in files.Items)
                    {
                        Console.WriteLine(
                            $"  {file.Name,-50} {FormatCount(file.Rows),10} row(s)  {file.SizeBytes,12:N0} byte(s)  " +
                            $"{(file.LastRun ? "last-run" : string.Empty)}  {file.Path ?? string.Empty}");
                    }

                    Console.WriteLine($"({files.Items.Count} of {files.Total} file(s))");
                    return 0;
                }
            }
        }).ConfigureAwait(false);
    }

    // ---- datasources (remote compute tasks) ----------------------------------------------------------------

    private static readonly Dictionary<string, string> ComputeOperationsBySub = new(StringComparer.Ordinal)
    {
        ["test"] = "testConnection",
        ["databases"] = "listDatabases",
        ["schemas"] = "listSchemas",
        ["objects"] = "listObjects",
        ["search"] = "searchObjects",
        ["introspect"] = "introspectObject",
        ["detect-unique-key"] = "detectUniqueKey",
    };

    /// <summary>
    /// 'sqlflow datasources ...': the remote twins of the local 'catalog' and 'detect-unique-key' verbs,
    /// executed as queued compute tasks on a worker node INSIDE the network (the same mechanism the GUI's
    /// browse page uses), so no direct database reachability is needed from this machine. 'list' shows the
    /// estate's declared references; test/databases/schemas/objects/search/introspect/detect-unique-key queue
    /// an operation against a reference and wait for the worker's result (JSON on stdout; --no-wait prints the
    /// task id instead); 'tasks', 'task &lt;id&gt;', and 'cancel &lt;id&gt;' manage the queue. Ctrl+C on a
    /// waited task requests its cancellation before exiting 130.
    /// </summary>
    public static async Task<int> DatasourcesAsync(string[] positional, string[] args)
    {
        var sub = positional.Length > 1 ? positional[1].ToLowerInvariant() : "list";
        if (sub is not ("list" or "tasks" or "task" or "cancel") && !ComputeOperationsBySub.ContainsKey(sub))
        {
            Console.Error.WriteLine(
                "ERROR  'datasources' supports: list, test, databases, schemas, objects, search, introspect, detect-unique-key, tasks, task <id>, cancel <id>.");
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
                    var datasources = await client.ListDatasourcesAsync(ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(datasources, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"{"REFERENCE",-48}  {"KIND",-10}  {"BROWSABLE",-9}  USED BY (src/trg)");
                    foreach (var datasource in datasources)
                    {
                        Console.WriteLine(
                            $"{Truncate(datasource.Reference, 48),-48}  {datasource.Kind ?? "-",-10}  {(datasource.Resolvable ? "yes" : "no"),-9}  " +
                            $"{datasource.SourcePipelines}/{datasource.TargetPipelines}");
                    }

                    Console.WriteLine($"({datasources.Count} reference(s))");
                    return 0;
                }

                case "tasks":
                {
                    var tasks = await client.ListComputeTasksAsync(
                        Program.GetOption(args, "--status"), Program.GetOption(args, "--ref"), Program.GetOption(args, "--operation"),
                        Program.ParseIntOption(args, 1, "--page"), Program.ParseIntOption(args, 50, "--page-size"), ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(tasks, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Console.WriteLine($"{"TASK",-36}  {"OPERATION",-18}  {"STATUS",-9}  {"TARGET",-30}  {"NODE",-16}  ENQUEUED (UTC)");
                    foreach (var task in tasks.Items)
                    {
                        Console.WriteLine(
                            $"{task.TaskId,-36}  {task.Operation,-18}  {task.Status,-9}  {Truncate(task.Target ?? "-", 30),-30}  " +
                            $"{Truncate(task.ClaimedByNode ?? "-", 16),-16}  {task.EnqueuedUtc:yyyy-MM-dd HH:mm:ss}");
                    }

                    Console.WriteLine($"({tasks.Items.Count} of {tasks.Total} task(s))");
                    return 0;
                }

                case "task":
                {
                    if (!TryRequireId(positional, "datasources task", out var taskId))
                    {
                        return 1;
                    }

                    var task = await client.GetComputeTaskAsync(taskId, waitMs: 0, ct).ConfigureAwait(false);
                    if (task is null)
                    {
                        Console.Error.WriteLine($"ERROR  no compute task '{taskId}'.");
                        return 1;
                    }

                    Console.WriteLine(JsonSerializer.Serialize(task, ControlPlaneClient.JsonIndented));
                    return task.Status == "failed" ? 1 : 0;
                }

                case "cancel":
                {
                    if (!TryRequireId(positional, "datasources cancel", out var taskId))
                    {
                        return 1;
                    }

                    return PrintCancelOutcome(await client.CancelComputeTaskAsync(taskId, ct).ConfigureAwait(false), "compute task", taskId);
                }

                default:
                    return await RunComputeOperationAsync(client, ComputeOperationsBySub[sub], args, json, ct).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Queues one compute operation and (unless --no-wait) long-polls it to its terminal state,
    /// printing the worker's JSON result. The result IS the dataset: pretty JSON on stdout by design, equally
    /// consumable by a human, a script, or an LLM.</summary>
    private static async Task<int> RunComputeOperationAsync(
        ControlPlaneClient client, string operation, string[] args, bool json, CancellationToken ct)
    {
        var reference = Program.GetOption(args, "--ref");
        if (string.IsNullOrWhiteSpace(reference))
        {
            Console.Error.WriteLine("ERROR  a datasource operation requires --ref <${env:NAME} reference or @alias> (see 'sqlflow datasources list').");
            return 1;
        }

        // --object accepts schema.name (or just the name when --schema is given), mirroring the local verbs.
        string? schema = Program.GetOption(args, "--schema");
        string? objectName = Program.GetOption(args, "--object");
        if (objectName is not null && objectName.Contains('.', StringComparison.Ordinal) && schema is null)
        {
            var parts = objectName.Split('.', 2);
            schema = parts[0];
            objectName = parts[1];
        }

        long? sample = Program.GetOption(args, "--sample") is { } s ? long.Parse(s, CultureInfo.InvariantCulture) : null;
        var request = new ComputeTaskRequest(
            reference, operation, Program.GetOption(args, "--kind"), Program.GetOption(args, "--pool"),
            Program.GetOption(args, "--database"), schema, objectName,
            Program.GetOption(args, "--like"), Program.GetOption(args, "--term"),
            IncludeSystem: args.Contains("--include-system"),
            Offset: Program.ParseIntOption(args, 0, "--offset"),
            Limit: Program.ParseIntOption(args, 200, "--limit"),
            SampleSize: sample,
            MaxKeyColumns: Program.ParseIntOption(args, 4, "--max-columns"),
            MaxCandidates: Program.ParseIntOption(args, 5, "--max-candidates"),
            VerifyCandidates: !args.Contains("--no-verify"),
            TrustDeclaredKeys: !args.Contains("--no-metadata"));

        var accepted = await client.CreateComputeTaskAsync(request, ct).ConfigureAwait(false);
        Note(json: true, $"task {accepted.TaskId} {accepted.Status} ({operation} on {reference}).");
        if (args.Contains("--no-wait"))
        {
            Console.WriteLine(accepted.TaskId);
            return 0;
        }

        try
        {
            while (true)
            {
                var task = await client.GetComputeTaskAsync(accepted.TaskId, waitMs: 20000, ct).ConfigureAwait(false)
                           ?? throw new SqlFlowException($"compute task {accepted.TaskId} vanished from the queue (expired before a node claimed it?).");
                switch (task.Status)
                {
                    case "succeeded":
                        Console.WriteLine(task.Result is { } result
                            ? JsonSerializer.Serialize(result, ControlPlaneClient.JsonIndented)
                            : "{}");
                        return 0;
                    case "failed":
                        Console.Error.WriteLine($"FAILED  {operation}: {task.Error ?? "(no error recorded)"}");
                        return 1;
                    case "cancelled":
                        Console.Error.WriteLine($"CANCELLED  {operation} was cancelled.");
                        return 1;
                    default:
                        Console.Error.WriteLine($"  {task.Status}{(task.ClaimedByNode is null ? " (waiting for a node; is a worker serving this pool?)" : $" on {task.ClaimedByNode}")}...");
                        continue;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C: request the queued/running task's cancellation so no node burns work for a caller that left.
            await client.CancelComputeTaskAsync(accepted.TaskId, CancellationToken.None).ConfigureAwait(false);
            Console.Error.WriteLine($"CANCELLED  interrupted; cancellation of task {accepted.TaskId} was requested.");
            return 130;
        }
    }

    // ---- search -------------------------------------------------------------------------------------------

    /// <summary>'sqlflow search &lt;term&gt;': the global catalog search. Default is the combined view (every
    /// category counted, top hits previewed); one of --objects/--columns/--definitions/--files/--flows narrows
    /// to a paged single category.</summary>
    public static async Task<int> SearchAsync(string[] positional, string[] args)
    {
        var term = positional.Length > 1 ? positional[1] : null;
        if (string.IsNullOrWhiteSpace(term))
        {
            Console.Error.WriteLine("ERROR  'search' requires a term: sqlflow search <term> [--objects|--columns|--definitions|--files|--flows].");
            return 1;
        }

        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        var page = Program.ParseIntOption(args, 1, "--page");
        var pageSize = Program.ParseIntOption(args, 50, "--page-size");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            if (args.Contains("--objects"))
            {
                var hits = await client.SearchAsync<ObjectHitDto>("objects", "name", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.Kind,-10} {h.Name,-40} {h.ServerRef}|{h.Database}|{h.Schema}  [{h.Key}]");
            }

            if (args.Contains("--columns"))
            {
                var hits = await client.SearchAsync<ColumnHitDto>("columns", "name", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.ColumnName,-32} {h.DataType ?? "-",-16} {(h.Nullable ? "null" : "not null"),-8}  {h.ObjectName}  [{h.ObjectKey}]");
            }

            if (args.Contains("--definitions"))
            {
                var hits = await client.SearchAsync<DefinitionHitDto>("definitions", "q", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.Kind,-10} {h.Name,-40} ({h.Source})  ...{Truncate(h.Snippet, 80)}...");
            }

            if (args.Contains("--files"))
            {
                var hits = await client.SearchAsync<FileHitDto>("files", "name", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.Name,-46} {FormatCount(h.Rows),10} row(s)  {h.FlowName}  run {h.RunId}");
            }

            if (args.Contains("--flows"))
            {
                var hits = await client.SearchAsync<FlowHitDto>("flows", "q", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.Kind,-5} {h.Name,-40} [{h.RepoName}] {h.RelativePath} (matched {h.MatchedIn})");
            }

            if (args.Contains("--flow-columns"))
            {
                var hits = await client.SearchAsync<FlowColumnHitDto>("flow-columns", "name", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.ColumnName,-32} {h.DataType ?? "-",-16} {h.FlowName,-32} ({h.Kind}, matched {h.MatchedIn})");
            }

            if (args.Contains("--statements"))
            {
                var hits = await client.SearchAsync<StatementHitDto>("statements", "q", term, page, pageSize, ct).ConfigureAwait(false);
                return PrintHits(json, hits, h => $"  {h.FlowName,-40} {h.Step,-20} {FormatCount(h.Occurrences),8}x  ...{Truncate(h.Snippet, 60)}...");
            }

            var all = await client.SearchAllAsync(term, ct).ConfigureAwait(false);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(all, ControlPlaneClient.JsonIndented));
                return 0;
            }

            Console.WriteLine($"objects ({all.Objects.Total}):");
            foreach (var hit in all.Objects.Items)
            {
                Console.WriteLine($"  {hit.Kind,-10} {hit.Name}  ({hit.ServerRef}|{hit.Database}|{hit.Schema})");
            }

            Console.WriteLine($"columns ({all.Columns.Total}):");
            foreach (var hit in all.Columns.Items)
            {
                Console.WriteLine($"  {hit.ColumnName,-32} {hit.DataType ?? "-",-16} on {hit.ObjectName}");
            }

            Console.WriteLine($"definitions ({all.Definitions.Total}):");
            foreach (var hit in all.Definitions.Items)
            {
                Console.WriteLine($"  {hit.Kind,-10} {hit.Name}");
            }

            Console.WriteLine($"files ({all.Files.Total}):");
            foreach (var hit in all.Files.Items)
            {
                Console.WriteLine($"  {hit.Name}  ({hit.FlowName}, run {hit.RunId})");
            }

            Console.WriteLine($"flows ({all.Flows.Total}):");
            foreach (var hit in all.Flows.Items)
            {
                Console.WriteLine($"  {hit.Kind,-5} {hit.Name}  [{hit.RepoName}] {hit.RelativePath}");
            }

            Console.WriteLine($"flow columns ({all.FlowColumns.Total}):");
            foreach (var hit in all.FlowColumns.Items)
            {
                Console.WriteLine($"  {hit.ColumnName,-32} {hit.FlowName}  ({hit.Kind}, matched {hit.MatchedIn})");
            }

            Console.WriteLine($"executed sql ({all.Statements.Total}, last {all.StatementWindowDays} day(s)):");
            foreach (var hit in all.Statements.Items)
            {
                Console.WriteLine($"  {hit.FlowName,-40} {hit.Step}  ({FormatCount(hit.Occurrences)}x)");
            }

            // A multi-word term is matched word by word, so echoing the parsed tokens explains a surprising result
            // without the reader having to know the rule.
            if (all.Tokens.Count > 1)
            {
                Console.Error.WriteLine($"NOTE  matched every word of: {string.Join(" + ", all.Tokens)}");
            }

            Console.Error.WriteLine(
                "NOTE  narrow to one paged category with --objects, --columns, --definitions, --files, --flows, "
                + "--flow-columns, or --statements.");
            return 0;
        }).ConfigureAwait(false);
    }

    private static int PrintHits<T>(bool json, PagedResult<T> hits, Func<T, string> render)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(hits, ControlPlaneClient.JsonIndented));
            return 0;
        }

        foreach (var hit in hits.Items)
        {
            Console.WriteLine(render(hit));
        }

        Console.WriteLine($"({hits.Items.Count} of {hits.Total} hit(s))");
        return 0;
    }

    // ---- lineage (the graph as data) ----------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow lineage objects|edges|waves|script': the catalog's lineage graph as DATA. A console cannot
    /// draw the GUI's graph, but the dataset behind it prints fine, and with --json it is directly consumable
    /// by an LLM or an integration test: objects (the nodes), edges (who reads/writes what, attributed to
    /// flows), waves (the execution plan), and script (the code behind any node). The offline
    /// 'lineage &lt;folder&gt;' verb is untouched; these subcommands query the control plane's synced view.
    /// </summary>
    public static async Task<int> LineageRemoteAsync(string[] positional, string[] args)
    {
        var sub = positional[1].ToLowerInvariant();
        var url = RequireUrl(args);
        using var client = CreateAuthenticatedClient(url, args);
        var json = args.Contains("--json");
        var page = Program.ParseIntOption(args, 1, "--page");
        var pageSize = Program.ParseIntOption(args, 100, "--page-size");
        return await GuardedAsync(url, CancellationToken.None, async ct =>
        {
            switch (sub)
            {
                case "objects":
                {
                    var objects = await client.ListLineageObjectsAsync(
                        Program.GetOption(args, "--name"), Program.GetOption(args, "--server"),
                        Program.GetOption(args, "--database"), Program.GetOption(args, "--schema"),
                        Program.GetOption(args, "--kind"), page, pageSize, ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(objects, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var obj in objects.Items)
                    {
                        Console.WriteLine($"  {obj.Kind,-10} {obj.Name,-40} {obj.ServerRef}|{obj.Database}|{obj.Schema}  [{obj.Key}]");
                    }

                    Console.WriteLine($"({objects.Items.Count} of {objects.Total} object(s))");
                    return 0;
                }

                case "edges":
                {
                    var repo = await ResolveRepoAsync(client, args, ct).ConfigureAwait(false);
                    var edges = await client.ListLineageEdgesAsync(
                        repo.Id, null, Program.GetOption(args, "--object"), Program.GetOption(args, "--relation"),
                        Program.GetOption(args, "--tier"), page, pageSize, ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(edges, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var edge in edges.Items)
                    {
                        Console.WriteLine($"  {edge.Flow ?? edge.ViaModule ?? "?",-40} {edge.Relation,-6} {edge.ObjectName,-40} ({edge.Tier})  [{edge.ObjectKey}]");
                    }

                    Console.WriteLine($"({edges.Items.Count} of {edges.Total} edge(s) in [{repo.Name}])");
                    return 0;
                }

                case "waves":
                {
                    var repo = await ResolveRepoAsync(client, args, ct).ConfigureAwait(false);
                    var waves = await client.GetWavesAsync(repo.Id, ct).ConfigureAwait(false);
                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(waves, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    foreach (var wave in waves)
                    {
                        Console.WriteLine($"wave {wave.Wave} ({wave.Pipelines.Count} flow(s)):");
                        foreach (var pipeline in wave.Pipelines)
                        {
                            Console.WriteLine($"  {pipeline.Kind,-5} {pipeline.Name}");
                        }
                    }

                    Console.WriteLine($"({waves.Count} wave(s) in [{repo.Name}])");
                    return 0;
                }

                default: // script
                {
                    var key = positional.Length > 2 ? positional[2] : null;
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        Console.Error.WriteLine("ERROR  'lineage script' requires a node key or pipeline name: sqlflow lineage script <key|flow>.");
                        return 1;
                    }

                    var script = await client.GetNodeScriptAsync(key, ct).ConfigureAwait(false);
                    if (script is null)
                    {
                        Console.Error.WriteLine($"ERROR  no lineage node '{key}'.");
                        return 1;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(script, ControlPlaneClient.JsonIndented));
                        return 0;
                    }

                    Note(json: true, $"{script.Kind} '{script.Name ?? script.Key}' ({script.Language}{(script.Source is null ? string.Empty : $", {script.Source}")}):");
                    Console.WriteLine(script.Script ?? string.Empty);
                    return 0;
                }
            }
        }).ConfigureAwait(false);
    }

    // ---- doctor -------------------------------------------------------------------------------------------

    /// <summary>
    /// 'sqlflow doctor': one pass over everything a flow needs to run from THIS machine, so "why does it not
    /// work here" is answered by one command instead of a variable-by-variable hunt: which .sqlflow/env was
    /// found, which SQLFLOW_* variables resolve (presence only, values never printed), whether the control
    /// plane is live/ready and who the credential authenticates as, and whether the catalog database is
    /// reachable with a current schema. Unconfigured surfaces are SKIP, not failures; exit 1 only when a
    /// configured surface fails its probe.
    /// </summary>
    public static async Task<int> DoctorAsync(string[] args)
    {
        var failed = false;

        // The env file: re-discovering is safe (the process environment always wins over the file).
        var (applied, envFile) = LocalEnvFile.ApplyNearest(Directory.GetCurrentDirectory());
        Console.WriteLine(envFile is null
            ? "env file:      (none found from the current directory upward)"
            : $"env file:      {envFile} ({applied.Count} variable(s) newly applied)");

        Console.WriteLine("environment:");
        foreach (var name in new[]
                 {
                     "SQLFLOW_URL", "SQLFLOW_TOKEN", "SQLFLOW_CATALOG_DB", "SQLFLOW_CREDENTIALS_FILE",
                     "SQLFLOW_SOURCE", "SQLFLOW_REPO", "SQLFLOW_AZURE_AUTH",
                 })
        {
            var set = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
            Console.WriteLine($"  {name,-26} {(set ? "set" : "(unset)")}");
        }

        // The control plane, when configured.
        var urlRaw = Program.GetOption(args, "--url") ?? Environment.GetEnvironmentVariable("SQLFLOW_URL");
        if (string.IsNullOrWhiteSpace(urlRaw))
        {
            Console.WriteLine("control plane: SKIP (no --url / SQLFLOW_URL)");
        }
        else if (!Uri.TryCreate(urlRaw.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            Console.WriteLine($"control plane: FAIL ('{urlRaw}' is not an absolute http(s) URL)");
            failed = true;
        }
        else
        {
            using var client = CreateAuthenticatedClient(url, args);
            try
            {
                var (live, _) = await client.ProbeHealthAsync("/health/live", CancellationToken.None).ConfigureAwait(false);
                var (ready, _) = await client.ProbeHealthAsync("/health/ready", CancellationToken.None).ConfigureAwait(false);
                var healthy = live == HttpStatusCode.OK && ready == HttpStatusCode.OK;
                Console.WriteLine($"control plane: {(healthy ? "OK" : "FAIL")} ({url}: live {(int)live}, ready {(int)ready})");
                failed |= !healthy;

                if (client.BearerToken is { Length: > 0 })
                {
                    try
                    {
                        var identity = await client.GetIdentityAsync(CancellationToken.None).ConfigureAwait(false);
                        Console.WriteLine($"credential:    OK ({identity.Subject}, scopes: {string.Join(' ', identity.Scopes)}; from {DescribeTokenSource(args, url)})");
                    }
                    catch (SqlFlowException ex)
                    {
                        Console.WriteLine($"credential:    FAIL ({SecretHygiene.RedactedMessage(ex)})");
                        failed = true;
                    }
                }
                else
                {
                    Console.WriteLine("credential:    SKIP (no --token / SQLFLOW_TOKEN / stored login)");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"control plane: FAIL (cannot reach {url}: {SecretHygiene.RedactedMessage(ex)})");
                failed = true;
            }
        }

        // The shadow catalog, when configured (the direct-DB surface: db/worker/runs-cancel fallback).
        var catalogConnection = Environment.GetEnvironmentVariable("SQLFLOW_CATALOG_DB");
        if (string.IsNullOrWhiteSpace(catalogConnection))
        {
            Console.WriteLine("catalog db:    SKIP (no SQLFLOW_CATALOG_DB)");
        }
        else
        {
            try
            {
                var (appliedMigrations, pending) = await CatalogDatabase.StatusAsync(catalogConnection).ConfigureAwait(false);
                Console.WriteLine(pending.Count == 0
                    ? $"catalog db:    OK ({appliedMigrations.Count} migration(s) applied, schema current)"
                    : $"catalog db:    FAIL ({pending.Count} migration(s) pending; run 'sqlflow db migrate')");
                failed |= pending.Count != 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"catalog db:    FAIL ({SecretHygiene.RedactedMessage(ex)})");
                failed = true;
            }
        }

        Console.WriteLine(failed ? "doctor: problems found." : "doctor: everything configured checks out.");
        return failed ? 1 : 0;
    }

    // ---- shared -------------------------------------------------------------------------------------------

    /// <summary>Where the effective bearer credential came from, for whoami/doctor diagnostics.</summary>
    private static string DescribeTokenSource(string[] args, Uri url)
        => Program.GetOption(args, "--token") is not null ? "--token"
            : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQLFLOW_TOKEN")) ? "SQLFLOW_TOKEN"
            : CredentialStore.Load(url) is not null ? $"stored login ({CredentialStore.FilePath})"
            : "(none)";

    /// <summary>Resolves a positional repo argument (name or id) for the repos verbs; null (after the error
    /// line) when missing or unknown.</summary>
    private static async Task<RepoDto?> ResolveRepoArgumentAsync(
        ControlPlaneClient client, string[] positional, string verb, CancellationToken ct)
    {
        if (positional.Length < 3 || string.IsNullOrWhiteSpace(positional[2]))
        {
            Console.Error.WriteLine($"ERROR  '{verb}' requires a repo: sqlflow {verb} <name|id>.");
            return null;
        }

        var raw = positional[2];
        var repos = await client.ListReposAsync(ct).ConfigureAwait(false);
        var repo = Guid.TryParse(raw, out var id)
            ? repos.FirstOrDefault(r => r.Id == id)
            : repos.FirstOrDefault(r => string.Equals(r.Name, raw, StringComparison.OrdinalIgnoreCase));
        if (repo is null)
        {
            Console.Error.WriteLine(
                $"ERROR  no repo '{raw}'. Known repos: {string.Join(", ", repos.Select(r => r.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(15))}.");
        }

        return repo;
    }
}
