using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Dispatch;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Dispatch.ManagedWorkers;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Managed workers in the control plane: off by default and then invisible, refused at startup when a setting cannot
/// work or no launcher answers to the configured name, its state readable from the API, and the credential a launcher
/// gives a worker it starts on this machine accepted by the node protocol and nothing else. None of these needs a
/// catalog: the host starts against an unreachable one, so dispatch never activates and the fleet stays passive.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ManagedWorkersTests
{
    // ------------------------------------------------------------------------------------------------ settings -----

    [Fact]
    public void Off_ValidatesNothing_SoNoDefaultStopsAHost()
        => new ManagedWorkersOptions { MinWorkers = 0, Launcher = null, TickSeconds = 0 }.Validate();

    [Fact]
    public void On_WithoutALauncher_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ManagedWorkersOptions { Enabled = true }.Validate());
        Assert.Contains("ControlPlane:ManagedWorkers:Launcher is required", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10, "ControlPlane:ManagedWorkers:MinWorkers must be at least 1")]
    [InlineData(3, 2, "ControlPlane:ManagedWorkers:MaxWorkers must be between MinWorkers (3) and 100")]
    [InlineData(1, 101, "ControlPlane:ManagedWorkers:MaxWorkers must be between MinWorkers (1) and 100")]
    public void TheDefaultPoolsBounds_AreChecked(int min, int max, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ManagedWorkersOptions { Enabled = true, Launcher = "jobs", MinWorkers = min, MaxWorkers = max }.Validate());
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedPoolsBounds_AreChecked_UnderItsOwnName()
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs" };
        options.Pools["heavy"] = new ManagedPoolOptions { MinWorkers = 0 };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ControlPlane:ManagedWorkers:Pools:heavy:MinWorkers must be at least 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PoolsWhoseWorkersWouldShareAName_AreRefused()
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs" };
        options.Pools["Heavy Jobs"] = new ManagedPoolOptions();
        options.Pools["heavy-jobs"] = new ManagedPoolOptions();

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("would both name their workers 'heavy-jobs'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedPoolCalledDefault_IsRefused()
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs" };
        options.Pools["default"] = new ManagedPoolOptions();

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("the default pool and 'default'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Worker")]
    [InlineData("1worker")]
    [InlineData("worker-")]
    [InlineData("work_er")]
    [InlineData("a-prefix-far-too-long-for-a-name")]
    public void ANamePrefixThatWouldNotMakeValidNames_IsRefused(string prefix)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ManagedWorkersOptions { Enabled = true, Launcher = "jobs", NamePrefix = prefix }.Validate());
        Assert.Contains("ControlPlane:ManagedWorkers:NamePrefix must be", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ManagedWorkersOptions.TickSeconds), 0, "TickSeconds must be between 1 and 60")]
    [InlineData(nameof(ManagedWorkersOptions.TickSeconds), 61, "TickSeconds must be between 1 and 60")]
    [InlineData(nameof(ManagedWorkersOptions.IdleMinutes), 0, "IdleMinutes must be at least 1")]
    [InlineData(nameof(ManagedWorkersOptions.StartTimeoutMinutes), 0, "StartTimeoutMinutes must be at least 1")]
    [InlineData(nameof(ManagedWorkersOptions.ExitTimeoutMinutes), 0, "ExitTimeoutMinutes must be at least 1")]
    [InlineData(nameof(ManagedWorkersOptions.MaxLifetimeHours), 0, "MaxLifetimeHours must be at least 1")]
    [InlineData(nameof(ManagedWorkersOptions.MaxLaunchesPerTick), 0, "MaxLaunchesPerTick must be between 1 and 100")]
    public void ATimingThatCannotWork_IsRefused_NamingTheSetting(string setting, int value, string message)
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs" };
        typeof(ManagedWorkersOptions).GetProperty(setting)!.SetValue(options, value);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains($"ControlPlane:ManagedWorkers:{message}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ManagedWorkersOptions.MaxLifetimeHours), int.MaxValue, "MaxLifetimeHours must be at most 8760")]
    [InlineData(nameof(ManagedWorkersOptions.IdleMinutes), int.MaxValue, "IdleMinutes must be at most 10080")]
    [InlineData(nameof(ManagedWorkersOptions.StartTimeoutMinutes), 100000, "StartTimeoutMinutes must be at most 1440")]
    [InlineData(nameof(ManagedWorkersOptions.ExitTimeoutMinutes), 100000, "ExitTimeoutMinutes must be at most 1440")]
    public void AValueTooLargeToBeADuration_IsRefusedByName_NotOverflowed(string setting, int value, string message)
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs" };
        typeof(ManagedWorkersOptions).GetProperty(setting)!.SetValue(options, value);

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains($"ControlPlane:ManagedWorkers:{message}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildTag_IsTheSameForTheSameAssemblies_AndDifferentForOthers()
    {
        var first = Directory.CreateTempSubdirectory("sqlflow-build-tag-");
        var second = Directory.CreateTempSubdirectory("sqlflow-build-tag-");
        try
        {
            File.WriteAllBytes(Path.Combine(first.FullName, "SqlFlow.Core.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(first.FullName, "SqlFlow.Node.dll"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(first.FullName, "Other.dll"), [7]);
            File.WriteAllBytes(Path.Combine(second.FullName, "SqlFlow.Core.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(second.FullName, "SqlFlow.Node.dll"), [4, 5, 6]);

            var tag = ControlPlaneBuild.Tag(first.FullName);
            Assert.Matches("^[0-9a-f]{4}$", tag);
            Assert.Equal(tag, ControlPlaneBuild.Tag(second.FullName)); // other assemblies do not count

            File.WriteAllBytes(Path.Combine(second.FullName, "SqlFlow.Node.dll"), [4, 5, 7]);
            Assert.NotEqual(tag, ControlPlaneBuild.Tag(second.FullName));
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void WithNoAssemblies_TheBuildTagIsTheUnknownOne()
    {
        var empty = Directory.CreateTempSubdirectory("sqlflow-build-tag-");
        try
        {
            Assert.Equal(WorkerNames.UnknownBuild, ControlPlaneBuild.Tag(empty.FullName));
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void ADeploymentsOwnBuild_WinsOverTheAssemblies()
    {
        var empty = Directory.CreateTempSubdirectory("sqlflow-build-tag-");
        try
        {
            var (tag, source, known) = ControlPlaneBuild.Resolve(" 4faca85 ", empty.FullName);
            Assert.Equal(ControlPlaneBuild.TagOf("4faca85"), tag);
            Assert.Matches("^[0-9a-f]{4}$", tag);
            Assert.True(known);
            Assert.Contains("the deployment's build '4faca85'", source, StringComparison.Ordinal);
            Assert.NotEqual(ControlPlaneBuild.TagOf("4faca85"), ControlPlaneBuild.TagOf("4faca86"));

            // Without one, the assemblies decide; none readable is a build nobody can tell apart, and says so.
            var (fallback, from, readable) = ControlPlaneBuild.Resolve("  ", empty.FullName);
            Assert.Equal(WorkerNames.UnknownBuild, fallback);
            Assert.False(readable);
            Assert.Contains("assemblies", from, StringComparison.Ordinal);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheSettings_CarryEveryPool_TheDefaultFirst()
    {
        var options = new ManagedWorkersOptions { Enabled = true, Launcher = "jobs", MinWorkers = 2, MaxWorkers = 6 };
        options.Pools["heavy"] = new ManagedPoolOptions { MinWorkers = 1, MaxWorkers = 3 };

        var settings = options.ToSettings(defaultRunSlots: 4, "ab12", TimeSpan.FromSeconds(45));

        Assert.Equal(
            [new ManagedPoolSettings(string.Empty, 2, 6), new ManagedPoolSettings("heavy", 1, 3)], settings.Pools);
        Assert.Equal(4, settings.DefaultRunSlots);
        Assert.Equal("ab12", settings.BuildTag);
        Assert.Equal(TimeSpan.FromSeconds(45), settings.PollFreshness);
    }

    // ---------------------------------------------------------------------------------------------------- host -----

    [Fact]
    public async Task Off_TheHostRunsNoFleet_AndTheApiSaysSo()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        Assert.Null(factory.Services.GetService<ManagedFleet>());
        Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), s => s is ManagedWorkersService);

        using var managed = await GetManagedAsync(factory, client);
        Assert.False(managed.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task On_TheHostRunsTheFleet_ThroughTheNamedLauncher()
    {
        await using var factory = Managed("fake").WithServices(s => s.AddSingleton<IWorkerLauncher>(new NamedLauncher("fake")));
        using var client = factory.CreateClient();

        var fleet = factory.Services.GetRequiredService<ManagedFleet>();
        Assert.Equal("fake", fleet.LauncherName);
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is ManagedWorkersService);

        using var managed = await GetManagedAsync(factory, client);
        Assert.True(managed.RootElement.GetProperty("enabled").GetBoolean());
        var status = managed.RootElement.GetProperty("status");
        Assert.Equal("fake", status.GetProperty("launcher").GetString());
        Assert.False(status.GetProperty("active").GetBoolean()); // no catalog, so this replica never owns dispatch
    }

    [Fact]
    public async Task TheLauncherName_IsMatchedRegardlessOfCase()
    {
        await using var factory = Managed("Jobs").WithServices(s => s.AddSingleton<IWorkerLauncher>(new NamedLauncher("jobs")));
        using var client = factory.CreateClient();

        Assert.Equal("jobs", factory.Services.GetRequiredService<ManagedFleet>().LauncherName);
    }

    [Fact]
    public void On_WithNoLauncherRegistered_TheHostRefusesToStart()
    {
        using var factory = Managed("jobs");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:Launcher is 'jobs', but this host registers no launcher at all", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void On_WithOnlyOtherLaunchers_TheHostRefusesToStart_NamingThem()
    {
        using var factory = Managed("jobs").WithServices(s => s.AddSingleton<IWorkerLauncher>(new NamedLauncher("process")));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("registers only 'process'", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void On_WithASettingThatCannotWork_TheHostRefusesToStart()
    {
        using var factory = Managed("fake").WithSetting("ControlPlane:ManagedWorkers:MinWorkers", "0");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ControlPlane:ManagedWorkers:MinWorkers must be at least 1", Flatten(ex), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------- node credential ----

    [Fact]
    public async Task ANodeCredential_IsAcceptedByTheNodeProtocol()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<TokenIssuer>();
        var credential = issuer.IssueNodeCredential("worker-default-0123456789", TimeSpan.FromHours(13), DateTime.UtcNow);

        using var response = await PollAsync(client, credential.Token);

        // Authenticated and authorised: the only refusal left is that this replica does not own dispatch (no catalog).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.InRange(credential.ExpiresUtc, DateTime.UtcNow.AddHours(12.9), DateTime.UtcNow.AddHours(13.1));
    }

    [Fact]
    public async Task ANodeCredential_ReachesNothingButTheNodeProtocol()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var credential = factory.Services.GetRequiredService<TokenIssuer>()
            .IssueNodeCredential("worker-default-0123456789", TimeSpan.FromHours(1), DateTime.UtcNow);

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/v1/nodes/managed"),
            (HttpMethod.Get, "/api/v1/nodes"),
            (HttpMethod.Put, "/api/v1/nodes/pools/scale"),
            (HttpMethod.Get, "/api/v1/me/tokens"),
        })
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            if (method == HttpMethod.Put)
            {
                request.Content = JsonContent.Create(new { pool = "", minReplicas = 5 });
            }

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(NodeCredentialMiddleware.DeniedTitle, problem.RootElement.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task ANodeCredential_CannotPollUnderAnotherNodesHeader()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var credential = factory.Services.GetRequiredService<TokenIssuer>()
            .IssueNodeCredential("worker-default-0123456789", TimeSpan.FromHours(1), DateTime.UtcNow);

        using var response = await PollAsync(client, credential.Token, header: "worker-default-9999999999");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(NodeCredentialMiddleware.DeniedTitle, problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ANodeCredential_CannotSpeakForAnotherNodeInTheRequest()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var credential = factory.Services.GetRequiredService<TokenIssuer>()
            .IssueNodeCredential("worker-default-0123456789", TimeSpan.FromHours(1), DateTime.UtcNow);

        using var response = await PollAsync(client, credential.Token, body: "worker-default-9999999999");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("the node it was issued for", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenWithoutTheNodeScope_CannotPoll()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<TokenIssuer>().Issue("reader", ["read"], DateTime.UtcNow);

        using var response = await PollAsync(client, token.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void ANodeCredential_NeedsASubjectAndALifetime()
    {
        using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var issuer = factory.Services.GetRequiredService<TokenIssuer>();

        Assert.Throws<ArgumentException>(() => issuer.IssueNodeCredential(" ", TimeSpan.FromHours(1), DateTime.UtcNow));
        Assert.Throws<ArgumentNullException>(() => issuer.IssueNodeCredential(null!, TimeSpan.FromHours(1), DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => issuer.IssueNodeCredential("worker", TimeSpan.Zero, DateTime.UtcNow));
    }

    // ------------------------------------------------------------------------------------------------- support -----

    private static ControlPlaneAppFactory Managed(string launcher)
        => new ControlPlaneAppFactory()
            .WithSetting("ControlPlane:ManagedWorkers:Enabled", "true")
            .WithSetting("ControlPlane:ManagedWorkers:Launcher", launcher)
            .WithSetting("ControlPlane:Worker:Enabled", "false");

    private static async Task<JsonDocument> GetManagedAsync(ControlPlaneAppFactory factory, HttpClient client)
    {
        var token = factory.Services.GetRequiredService<TokenIssuer>().Issue("reader", ["read"], DateTime.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/nodes/managed", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PollAsync(
        HttpClient client, string token, string header = "worker-default-0123456789", string body = "worker-default-0123456789")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(NodeProtocol.RoutePrefix + "/poll", UriKind.Relative))
        {
            Content = JsonContent.Create(new NodePollRequest(body, "test", [], 4, 4, 2, 2, [], [], DateTime.UtcNow, 0)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(NodeProtocol.NodeHeader, header);
        return client.SendAsync(request);
    }

    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(Flatten));
            }
        }

        return string.Join(" | ", messages);
    }

    /// <summary>A launcher that answers to a name and has nothing to start: these tests never reach dispatch.</summary>
    private sealed class NamedLauncher(string name) : IWorkerLauncher
    {
        public string Name => name;

        public Task<IReadOnlyList<LaunchedWorker>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LaunchedWorker>>([]);

        public Task LaunchAsync(WorkerLaunch launch, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(string workerName, CancellationToken ct) => Task.CompletedTask;
    }
}
