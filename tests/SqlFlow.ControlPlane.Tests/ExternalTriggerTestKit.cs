using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>One flow of a test estate: its name, the schedule it joins, and its batch.</summary>
internal sealed record EstateFlow(string Name, string Schedule, string? Batch = null);

/// <summary>
/// A throwaway estate for the external trigger tests: a git repository of csv flows that join named schedules (declared
/// in a <c>schedules.yaml</c> with a yearly cron, so the scheduler never fires them on its own), registered as a repo
/// source and synced by the host's managed sync, plus everything the tests make against it (triggers, requests, runs).
/// Disposing removes every row it made and the repository itself.
/// </summary>
internal sealed class TriggerEstate : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _cs;

    private TriggerEstate(string cs, string repoName, string gitDir, string branch)
    {
        _cs = cs;
        RepoName = repoName;
        GitDir = gitDir;
        Branch = branch;
    }

    public string RepoName { get; }

    public string GitDir { get; }

    public string Branch { get; }

    public Guid RepoId => FlowIdentity.FromName(RepoName);

    public Guid SourceId => FlowIdentity.FromName($"reposource/{RepoName}");

    /// <summary>Seeds the repository with <paramref name="flows"/> and a library of every schedule they join plus
    /// <paramref name="extraSchedules"/> (raw YAML lines under <c>schedules:</c>), committed on its default branch.</summary>
    public static TriggerEstate Seed(string cs, string prefix, IReadOnlyList<EstateFlow> flows, params string[] extraSchedules)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = $"{prefix}_{suffix}";
        var gitDir = Path.Combine(Path.GetTempPath(), "sqlflow_trg_" + suffix);
        Directory.CreateDirectory(gitDir);
        Repository.Init(gitDir);
        Directory.CreateDirectory(Path.Combine(gitDir, "flows"));
        foreach (var flow in flows)
        {
            File.WriteAllText(Path.Combine(gitDir, "flows", flow.Name + ".flow.yaml"), FlowYaml(flow));
        }

        var declared = extraSchedules.Select(line => line[..line.IndexOf(':', StringComparison.Ordinal)].Trim()).ToHashSet(StringComparer.Ordinal);
        var schedules = flows.Select(f => f.Schedule).Distinct(StringComparer.Ordinal).Where(s => !declared.Contains(s))
            .Select(s => $"  {s}: {{ cron: \"0 0 1 1 *\", timezone: \"UTC\" }}")
            .Concat(extraSchedules.Select(line => "  " + line));
        File.WriteAllText(Path.Combine(gitDir, "estate.schedules.yaml"), "schedules:\n" + string.Join('\n', schedules) + "\n");

        using var repo = new Repository(gitDir);
        Commands.Stage(repo, "*");
        var signature = Signature();
        repo.Commit("initial estate", signature, signature);
        return new TriggerEstate(cs, repoName, gitDir, repo.Head.FriendlyName);
    }

    /// <summary>Commits <paramref name="content"/> at <paramref name="relativePath"/> on the tracked branch and returns the commit.</summary>
    public string Commit(string relativePath, string content)
    {
        var file = Path.Combine(GitDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        using var repo = new Repository(GitDir);
        Commands.Stage(repo, "*");
        var signature = Signature();
        return repo.Commit("change " + relativePath, signature, signature).Sha;
    }

    /// <summary>Commits on a branch of its own, off the tracked branch, which the tracked branch never contains, and
    /// comes back to the tracked branch. Returns the side commit.</summary>
    public string CommitOnSideBranch(string branch, string relativePath, string content)
    {
        using var repo = new Repository(GitDir);
        var side = repo.CreateBranch(branch);
        Commands.Checkout(repo, side);
        var file = Path.Combine(GitDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        Commands.Stage(repo, "*");
        var signature = Signature();
        var sha = repo.Commit("side " + relativePath, signature, signature).Sha;
        Commands.Checkout(repo, repo.Branches[Branch]);
        return sha;
    }

    /// <summary>Registers the repository as a repo source and waits for its first sync.</summary>
    public async Task<RepoSourceDto> RegisterAsync(HttpClient client, string token)
    {
        using (var register = await Send(client, token, HttpMethod.Post, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest(RepoName, GitDir, Branch, 3600, true)))
        {
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        }

        return await WaitForSyncAsync(client, token, previous: null);
    }

    /// <summary>Waits until the repo source records a sync past <paramref name="previous"/> (any sync when null).</summary>
    public async Task<RepoSourceDto> WaitForSyncAsync(HttpClient client, string token, DateTime? previous)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var list = await Get<PagedResult<RepoSourceDto>>(client, token, "/api/v1/repos/sources?pageSize=200");
            if (list.Items.FirstOrDefault(s => s.Id == SourceId) is { LastSyncedSha: not null, SyncPending: false } source
                && (previous is null || source.LastSyncUtc > previous))
            {
                return source;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Repo source '{RepoName}' did not sync within a minute.");
    }

    /// <summary>Creates a trigger through the admin API and returns what was written (the key, for a key trigger).</summary>
    public async Task<ExternalTriggerWritten> CreateTriggerAsync(
        HttpClient client, string adminToken, string name, IReadOnlyList<string>? schedules = null, bool allSchedules = false,
        string kind = "key", ExternalTriggerGitHubDto? gitHub = null)
    {
        using var response = await Send(client, adminToken, HttpMethod.Post, "/api/v1/external-triggers",
            new ExternalTriggerWriteRequest(name, kind, RepoName, "test trigger", allSchedules, schedules ?? [], gitHub));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ExternalTriggerWritten>(Json))!;
    }

    /// <summary>Exchanges a trigger key for its credential.</summary>
    public static async Task<string> ExchangeKeyAsync(HttpClient client, string key)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/trigger/key", UriKind.Relative), new KeyTriggerExchangeRequest(key));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TriggerSessionResponse>(Json))!.AccessToken;
    }

    /// <summary>Makes a request and returns its answer's status and body.</summary>
    public static async Task<(HttpStatusCode Status, TriggerRequestDto? Request, string Body)> RequestAsync(
        HttpClient client, string token, CreateTriggerRequest body)
    {
        using var response = await Send(client, token, HttpMethod.Post, "/api/v1/trigger-requests", body);
        var text = await response.Content.ReadAsStringAsync();
        var dto = response.IsSuccessStatusCode ? JsonSerializer.Deserialize<TriggerRequestDto>(text, Json) : null;
        return (response.StatusCode, dto, text);
    }

    /// <summary>Waits until request <paramref name="id"/> satisfies <paramref name="until"/>, and returns its detail.</summary>
    public static async Task<TriggerRequestDetailDto> WaitAsync(
        HttpClient client, string token, Guid id, Func<TriggerRequestDetailDto, bool> until, int seconds = 60)
    {
        TriggerRequestDetailDto? last = null;
        for (var attempt = 0; attempt < seconds * 4; attempt++)
        {
            last = await Get<TriggerRequestDetailDto>(client, token, $"/api/v1/trigger-requests/{id:D}");
            if (until(last))
            {
                return last;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Trigger request {id:D} did not reach the expected state; it is {last?.Request.State} ({last?.Request.Detail}).");
    }

    /// <summary>Waits until request <paramref name="id"/> has ended.</summary>
    public static Task<TriggerRequestDetailDto> WaitForEndAsync(HttpClient client, string token, Guid id, int seconds = 60)
        => WaitAsync(client, token, id, d => TriggerRequestStates.IsTerminal(d.Request.State), seconds);

    /// <summary>Sets a run as its node would have: running, or ended with <paramref name="status"/>.</summary>
    public async Task SetRunAsync(Guid runId, string status, string? error = null, long? rows = null)
    {
        await using var db = CatalogDatabase.Create(_cs);
        var now = DateTime.UtcNow;
        var terminal = RunStatuses.IsTerminal(status);
        await db.Runs.Where(r => r.RunId == runId).ExecuteUpdateAsync(r => r
            .SetProperty(x => x.Status, status)
            .SetProperty(x => x.Success, status == RunStatuses.Succeeded)
            .SetProperty(x => x.StartUtc, x => x.StartUtc ?? now.AddSeconds(-3))
            .SetProperty(x => x.EndUtc, terminal ? now : null)
            .SetProperty(x => x.DurationSeconds, terminal ? 3.0 : null)
            .SetProperty(x => x.RowsLoaded, rows)
            .SetProperty(x => x.Error, error)
            .SetProperty(x => x.WrittenUtc, now));
    }

    /// <summary>The runs of a fire, by group or by run.</summary>
    public async Task<IReadOnlyList<CatalogRun>> RunsAsync(TriggerRequestScheduleDto schedule)
    {
        await using var db = CatalogDatabase.Create(_cs);
        return await db.Runs.AsNoTracking()
            .Where(r => schedule.GroupId != null ? r.GroupId == schedule.GroupId : r.RunId == schedule.RunId)
            .OrderBy(r => r.FlowName)
            .ToListAsync();
    }

    /// <summary>Ends every run of <paramref name="schedule"/>'s fire with <paramref name="status"/>.</summary>
    public async Task EndRunsAsync(TriggerRequestScheduleDto schedule, string status)
    {
        foreach (var run in await RunsAsync(schedule))
        {
            await SetRunAsync(run.RunId, status, status == RunStatuses.Failed ? "the target refused the load" : null, rows: 10);
        }
    }

    /// <summary>The request's whole trace, in order.</summary>
    public async Task<IReadOnlyList<CatalogActivityEvent>> TraceAsync(Guid requestId)
    {
        await using var db = CatalogDatabase.Create(_cs);
        var subject = requestId.ToString("D");
        return await db.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.TriggerRequest && e.SubjectKey == subject)
            .OrderBy(e => e.Id).ToListAsync();
    }

    /// <summary>Waits until the request's trace holds an event <paramref name="until"/> accepts.</summary>
    public async Task WaitForTraceAsync(Guid requestId, Func<CatalogActivityEvent, bool> until)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            if ((await TraceAsync(requestId)).Any(until))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Trigger request {requestId:D}'s trace never held the expected line.");
    }

    /// <summary>Runs <paramref name="work"/> on a catalog context of its own.</summary>
    public async Task<T> WithCatalogAsync<T>(Func<CatalogDbContext, Task<T>> work)
    {
        await using var db = CatalogDatabase.Create(_cs);
        return await work(db);
    }

    public static async Task<T> Get<T>(HttpClient client, string token, string url)
    {
        using var response = await Send(client, token, HttpMethod.Get, url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await using (var db = CatalogDatabase.Create(_cs))
        {
            var requestIds = await db.TriggerRequests.Where(r => r.RepoName == RepoName).Select(r => r.Id).ToListAsync();
            var subjects = requestIds.Select(id => id.ToString("D")).ToList();
            await db.ActivityEvents.Where(e => e.Kind == ActivityKinds.TriggerRequest && subjects.Contains(e.SubjectKey)).ExecuteDeleteAsync();
            await db.TriggerRequestRuns.Where(r => requestIds.Contains(r.RequestId)).ExecuteDeleteAsync();
            await db.TriggerRequestSchedules.Where(s => requestIds.Contains(s.RequestId)).ExecuteDeleteAsync();
            await db.TriggerRequests.Where(r => requestIds.Contains(r.Id)).ExecuteDeleteAsync();
            await db.ExternalTriggers.Where(t => t.RepoName == RepoName).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == RepoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == RepoId).ExecuteDeleteAsync();
            await db.ScheduleMembers.Where(m => m.RepoId == RepoId).ExecuteDeleteAsync();
            await db.ScheduleParents.Where(p => p.RepoId == RepoId).ExecuteDeleteAsync();
            await db.Schedules.Where(s => s.RepoId == RepoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == RepoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == RepoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == RepoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == RepoId).ExecuteDeleteAsync();
            await db.RepoSources.Where(s => s.Id == SourceId).ExecuteDeleteAsync();
            await db.ActivityEvents.Where(e => e.SubjectKey == SourceId.ToString()).ExecuteDeleteAsync();
        }

        DeleteDirectory(GitDir);
    }

    private static Signature Signature() => new("Test", "test@example.com", DateTimeOffset.UtcNow);

    private static string FlowYaml(EstateFlow flow) => $$"""
        name: {{flow.Name}}
        schedule: {{flow.Schedule}}
        {{(flow.Batch is null ? string.Empty : "batch: " + flow.Batch)}}
        source:
          type: csv
          location: ./data/{{flow.Name}}.csv
        target:
          connection: ${env:SQLFlowSinkConStr}
          schema: dbo
          table: {{flow.Name}}
        """;

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A git handle still closing on Windows; the folder is in the temp directory and harmless if left.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
