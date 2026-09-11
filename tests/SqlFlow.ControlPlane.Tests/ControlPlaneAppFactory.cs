using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Hosts the control plane in-memory through <see cref="WebApplicationFactory{TEntryPoint}"/> with a deterministic
/// test configuration: a fixed HS256 signing key (well over the 32-byte minimum the host validates at startup), a
/// fixed bootstrap secret so <c>POST /api/v1/auth/token</c> is mapped, and a catalog connection. The default
/// connection is a harmless non-empty placeholder: the host binds the catalog
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/> lazily, so it boots and serves the no-database surface
/// (auth, health/live, OpenAPI, 401s) without any SQL Server. DB-backed tests use <see cref="WithCatalog"/> to
/// point the same host at a real, migrated catalog.
/// </summary>
public sealed class ControlPlaneAppFactory : WebApplicationFactory<Program>
{
    /// <summary>A 64-character signing key: deterministic and comfortably above the 256-bit HS256 minimum.</summary>
    public const string SigningKey = "sqlflow-control-plane-test-signing-key-0123456789abcdef-PADDING";

    /// <summary>The bootstrap secret the test token endpoint accepts: at least 32 bytes, the minimum the host
    /// validates at startup.</summary>
    public const string BootstrapSecret = "test-bootstrap-secret-value-0123456789-PADDING";

    public const string Issuer = "sqlflow-control-plane-tests";

    public const string Audience = "sqlflow-tests";

    /// <summary>The stand-in catalog connection for tests that never reach a database. Endpoints that query the
    /// catalog past their authorization boundary (cancel-run, for instance) do issue a real connection attempt
    /// against it, and the catalog enables EF Core connection resiliency, so an unresolvable host name would be
    /// retried as transient for roughly a minute before the call gave up. A closed port on the loopback address
    /// is refused outright, which the retry strategy treats as non-transient, so those endpoints fail at once.</summary>
    private const string PlaceholderConnection =
        "Server=127.0.0.1,1;Database=unused;TrustServerCertificate=True;Connect Timeout=1;ConnectRetryCount=0";

    private string _catalogConnection = PlaceholderConnection;

    /// <summary>Every log line the host emits, captured in memory so a test can read the background worker's own
    /// diagnostics (which run off-thread and never reach xUnit output) when an assertion fails.</summary>
    public ConcurrentQueue<string> Logs { get; } = new();

    /// <summary>Points the host's catalog at <paramref name="catalogConnection"/> (used by DB-backed tests). Must be
    /// called before the first client is created, since the connection is read when the host is first built.</summary>
    public ControlPlaneAppFactory WithCatalog(string catalogConnection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogConnection);
        _catalogConnection = catalogConnection;
        return this;
    }

    /// <summary>Extra configuration a test needs applied before the host is built, for settings the shipped
    /// defaults leave off (a feature switch, for instance). Must be called before the first client.</summary>
    private readonly Dictionary<string, string> _settings = [];

    /// <summary>Overrides one configuration value for this host. Applied after the standard test settings, so
    /// a test can turn on a feature the deployed default leaves off.</summary>
    public ControlPlaneAppFactory WithSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);

        // The top-level Program reads ControlPlane configuration eagerly during host build and fails fast on a
        // missing signing key, so the test values must be present before that runs. UseSetting writes into the
        // web host's configuration, which WebApplication.CreateBuilder layers into builder.Configuration, so these
        // are resolved by the time Program binds and validates the ControlPlane options.
        builder.UseSetting("ControlPlane:Catalog:ConnectionReference", _catalogConnection);
        builder.UseSetting("ControlPlane:Jwt:SigningKey", SigningKey);
        builder.UseSetting("ControlPlane:Jwt:BootstrapSecret", BootstrapSecret);
        builder.UseSetting("ControlPlane:Jwt:Issuer", Issuer);
        builder.UseSetting("ControlPlane:Jwt:Audience", Audience);
        builder.UseSetting("ControlPlane:Jwt:AccessTokenMinutes", "60");
        // A generous rate limit for the in-memory test host: rate limiting is exercised by its own dedicated test,
        // not incidentally by suites that legitimately make many calls (e.g. the run-trigger test polls the run
        // detail endpoint until the worker finishes). The production default (120/60s) stays in the shipped config.
        builder.UseSetting("ControlPlane:RateLimit:PermitPerWindow", "1000000");
        builder.UseSetting("ControlPlane:RateLimit:WindowSeconds", "60");
        // A 1-second scheduler tick so the end-to-end scheduling test fires promptly; the shipped default is 15s.
        builder.UseSetting("ControlPlane:Scheduler:PollSeconds", "1");
        // A 1-second managed-sync tick so the end-to-end sync test runs promptly; the shipped default is 30s.
        builder.UseSetting("ControlPlane:ManagedSync:PollSeconds", "1");

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(new CapturingLoggerProvider(Logs));
            logging.SetMinimumLevel(LogLevel.Information);
        });
    }
}
