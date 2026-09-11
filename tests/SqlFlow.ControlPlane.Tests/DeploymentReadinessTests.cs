using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The scaled-deployment seams: reverse-proxy awareness (forwarded headers honored only from configured proxies,
/// with fail-fast validation of the trust list) and the API-only replica mode (Worker:Enabled=false), where the
/// HTTP tier answers but never claims runs, leaving compute to standalone workers scaled on queue depth.
/// </summary>
public sealed class DeploymentReadinessTests
{
    // ---- Proxy option validation (pure, no host) ----------------------------------------------------------------

    [Fact]
    public void ProxyOptions_Disabled_AcceptsAnything()
    {
        new ProxyOptions { Enabled = false }.Validate();
    }

    [Fact]
    public void ProxyOptions_EnabledWithoutAnyTrustedProxy_IsAStartupError()
    {
        var options = new ProxyOptions { Enabled = true };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("trusts no proxies", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.0/24")]
    [InlineData("fd00::/8")]
    public void ProxyOptions_ValidCidrs_Pass(string cidr)
    {
        new ProxyOptions { Enabled = true, KnownNetworks = [cidr] }.Validate();
        var (prefix, length) = ProxyOptions.ParseNetwork(cidr);
        Assert.NotNull(prefix);
        Assert.True(length >= 0);
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/33")]
    [InlineData("not-a-network/8")]
    [InlineData("fd00::/129")]
    public void ProxyOptions_MalformedCidrs_AreStartupErrors(string cidr)
    {
        var options = new ProxyOptions { Enabled = true, KnownNetworks = [cidr] };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ProxyOptions_MalformedProxyIp_IsAStartupError()
    {
        var options = new ProxyOptions { Enabled = true, KnownProxies = ["ingress.local"] };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ProxyOptions_NonPositiveForwardLimit_IsAStartupError()
    {
        var options = new ProxyOptions { Enabled = true, KnownProxies = ["10.0.0.1"], ForwardLimit = 0 };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // ---- The host boots and serves with the proxy configuration applied -----------------------------------------

    [Fact]
    public async Task Host_WithProxyEnabled_BootsAndServes()
    {
        using var factory = new ControlPlaneAppFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ControlPlane:Proxy:Enabled", "true");
            builder.UseSetting("ControlPlane:Proxy:KnownNetworks:0", "10.0.0.0/8");
            builder.UseSetting("ControlPlane:Proxy:KnownProxies:0", "127.0.0.1");
        });
        using var client = host.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // A forwarded header from an UNTRUSTED source is ignored, never a 500: the request just proceeds with
        // the connection's own address (TestServer has none), proving the middleware is active but selective.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/auth/providers", UriKind.Relative));
        request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- API-only replicas: Worker:Enabled=false answers HTTP but never claims runs ------------------------------

    [SkippableFact]
    public async Task ApiOnlyReplica_LeavesQueuedRunsForStandaloneWorkers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var flowName = $"api-only-{Guid.NewGuid():N}";

        try
        {
            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var host = factory.WithWebHostBuilder(builder =>
                builder.UseSetting("ControlPlane:Worker:Enabled", "false"));
            using var client = host.CreateClient();

            // The HTTP surface is fully alive.
            using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);

            // An UNTARGETED run (which the in-process worker would normally claim within its 2s poll) stays
            // queued on an API-only replica: compute is someone else's job here.
            Guid runId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                runId = (await RunQueueStore.EnqueueAsync(
                    db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal(RunStatuses.Queued, run.Status);
                Assert.Null(run.ClaimedByNode);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        }
    }
}
