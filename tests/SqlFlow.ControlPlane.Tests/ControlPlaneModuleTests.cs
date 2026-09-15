using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A host composes the control plane with modules through <see cref="IControlPlaneModule"/>: a module's options are bound
/// and validated from the host's configuration, its services and hosted services run inside the control plane, its
/// endpoints sit on the authenticated route groups under SQLFlow's own policies, and it can raise the request body limit.
/// None of this needs a database: every endpoint the tests call answers before any catalog read.
/// </summary>
public sealed class ControlPlaneModuleTests
{
    [Fact]
    public async Task AModuleEndpoint_ServesItsBoundOptionsAndServices_OnTheReadGroup()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello from the probe")
            .WithModules(new ProbeModule());
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, "read");

        using var request = Authorized(HttpMethod.Get, "/api/v1/probe/greeting", token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProbeGreeting>();
        Assert.Equal(new ProbeGreeting("hello from the probe", "probe"), body);
    }

    [Fact]
    public async Task AModuleEndpoint_RequiresAuthentication()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello")
            .WithModules(new ProbeModule());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/probe/greeting", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AModuleEndpointOnTheAdminGroup_RefusesACredentialWithoutTheAdminScope()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello")
            .WithModules(new ProbeModule());
        using var client = factory.CreateClient();
        var readToken = await IssueTokenAsync(client, "read", "operate");
        var adminToken = await IssueTokenAsync(client, "read", "admin");

        using var refused = await client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/probe/admin", readToken));
        using var allowed = await client.SendAsync(Authorized(HttpMethod.Get, "/api/v1/probe/admin", adminToken));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task AModuleHostedService_StartsWithTheHost()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello")
            .WithModules(new ProbeModule());
        using var client = factory.CreateClient();

        var state = factory.Services.GetRequiredService<ProbeState>();

        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AModule_RaisesTheRequestBodyLimit_AndAHostWithoutModulesKeepsTheServerDefault()
    {
        await using (var plain = new ControlPlaneAppFactory())
        {
            using var client = plain.CreateClient();
            var kestrel = plain.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
            Assert.Equal(ControlPlaneLimits.DefaultMaxRequestBodySize, kestrel.Limits.MaxRequestBodySize);
        }

        await using var withModule = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello")
            .WithModules(new ProbeModule());
        using var moduleClient = withModule.CreateClient();
        var raised = withModule.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        var limits = withModule.Services.GetRequiredService<ControlPlaneLimits>();

        Assert.Equal(ProbeModule.RequestBodyBytes, raised.Limits.MaxRequestBodySize);
        Assert.Equal("probe", limits.MaxRequestBodySizeRaisedBy);
    }

    [Fact]
    public async Task AModuleWithInvalidOptions_StopsTheHostFromStarting_NamingTheModuleAndSection()
    {
        await using var factory = new ControlPlaneAppFactory().WithModules(new ProbeModule());

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var module = FindModuleException(error);
        Assert.Equal("probe", module.ModuleName);
        Assert.Contains("'Probe'", module.Message, StringComparison.Ordinal);
        Assert.Contains("Probe:Greeting is required", module.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoModulesWithOneName_StopTheHostFromStarting()
    {
        await using var factory = new ControlPlaneAppFactory()
            .WithSetting("Probe:Greeting", "hello")
            .WithModules(new ProbeModule(), new ProbeModule());

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("registered twice", FindModuleException(error).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleThatFailsToMapItsEndpoints_IsNamedInTheError()
    {
        await using var factory = new ControlPlaneAppFactory().WithModules(new FailingMapModule());

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var module = FindModuleException(error);
        Assert.Equal("failing-map", module.ModuleName);
        Assert.Contains("failed to map its endpoints: no routes today", module.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Probe")]
    [InlineData("1probe")]
    [InlineData("probe_module")]
    [InlineData("probe module")]
    public void AnInvalidModuleName_IsRefused(string name)
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ControlPlaneModuleException>(
            () => services.AddControlPlaneModule(new NamedModule(name), EmptyConfiguration(), new TestEnvironment()));

        Assert.Contains("module", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(ControlPlaneLimits.MaxRequestBodySizeCeiling + 1)]
    public void ARequestBodyLimitOutOfRange_IsRefused_NamingTheModule(long bytes)
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ControlPlaneModuleException>(
            () => services.AddControlPlaneModule(new LimitModule(bytes), EmptyConfiguration(), new TestEnvironment()));

        Assert.Equal("limit", error.ModuleName);
        Assert.Contains(bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLargestRequestedLimitWins_AndASmallerRequestLeavesItInPlace()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddControlPlaneModule(new NamedModule("large", services => services.RaiseMaxRequestBodySize(100_000_000)), EmptyConfiguration(), new TestEnvironment());
        services.AddControlPlaneModule(new NamedModule("small", services => services.RaiseMaxRequestBodySize(40_000_000)), EmptyConfiguration(), new TestEnvironment());

        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<ControlPlaneLimits>();

        Assert.Equal(100_000_000, limits.MaxRequestBodySize);
        Assert.Equal("large", limits.MaxRequestBodySizeRaisedBy);
        Assert.Equal(100_000_000, provider.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
    }

    [Fact]
    public void AModuleOptionsValueThatDoesNotConvert_IsRefused_NamingTheModuleAndSection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Counted:Count"] = "many" })
            .Build();

        var error = Assert.Throws<ControlPlaneModuleException>(() => services.AddControlPlaneModule(
            new NamedModule("counted", module => module.AddOptions<CountedOptions>("Counted")), configuration, new TestEnvironment()));

        Assert.Equal("counted", error.ModuleName);
        Assert.Contains("'Counted'", error.Message, StringComparison.Ordinal);
    }

    private static ControlPlaneModuleException FindModuleException(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ControlPlaneModuleException module)
            {
                return module;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                return FindModuleException(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No ControlPlaneModuleException in: {error}");
    }

    private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    public sealed record ProbeGreeting(string Greeting, string Module);

    public sealed class ProbeOptions
    {
        public string? Greeting { get; set; }
    }

    public sealed class CountedOptions
    {
        public int Count { get; set; }
    }

    public sealed class ProbeState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProbeHostedService(ProbeState state) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            state.Started.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeModule : IControlPlaneModule
    {
        public const long RequestBodyBytes = 64L * 1024 * 1024;

        public string Name => "probe";

        public void ConfigureServices(ControlPlaneModuleServices services)
        {
            services.AddOptions<ProbeOptions>("Probe", options =>
            {
                if (string.IsNullOrWhiteSpace(options.Greeting))
                {
                    throw new InvalidOperationException("Probe:Greeting is required.");
                }
            });
            services.Services.AddSingleton<ProbeState>();
            services.AddHostedService<ProbeHostedService>();
            services.RaiseMaxRequestBodySize(RequestBodyBytes);
        }

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
        {
            endpoints.Read.MapGet("/probe/greeting", (IOptions<ProbeOptions> options) =>
                TypedResults.Ok(new ProbeGreeting(options.Value.Greeting!, endpoints.ModuleName)));
            endpoints.Admin.MapGet("/probe/admin", () => TypedResults.Ok());
        }
    }

    private sealed class FailingMapModule : IControlPlaneModule
    {
        public string Name => "failing-map";

        public void ConfigureServices(ControlPlaneModuleServices services)
        {
        }

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints) => throw new InvalidOperationException("no routes today");
    }

    private sealed class NamedModule(string name, Action<ControlPlaneModuleServices>? configure = null) : IControlPlaneModule
    {
        public string Name => name;

        public void ConfigureServices(ControlPlaneModuleServices services) => configure?.Invoke(services);

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
        {
        }
    }

    private sealed class LimitModule(long bytes) : IControlPlaneModule
    {
        public string Name => "limit";

        public void ConfigureServices(ControlPlaneModuleServices services) => services.RaiseMaxRequestBodySize(bytes);

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
        {
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
