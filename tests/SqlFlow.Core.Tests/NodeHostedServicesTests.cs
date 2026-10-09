using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.Cli.Hosting;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A <c>sqlflow worker</c> node runs from a plain service provider, so the hosted services a module registers for the node
/// (<see cref="CliServiceScope.Worker"/>) are started and stopped by <see cref="NodeHostedServices"/>, the way a generic host
/// does: started in registration order before the node takes work, stopped newest first after it drains, and a service
/// that fails to start stops the node with the service named and nothing left running.
/// </summary>
public sealed class NodeHostedServicesTests
{
    /// <summary>What every recording service did, in order, across one test.</summary>
    private sealed class Journal
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public void Add(string entry) => Entries.Enqueue(entry);

        public string[] All => [.. Entries];
    }

    private class Recording(Journal journal, string name, string? failOn = null) : IHostedService
    {
        protected Journal Journal => journal;

        protected string ServiceName => name;

        protected void Step(string step)
        {
            journal.Add($"{name}.{step}");
            if (string.Equals(step, failOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{name} cannot {step}: password=hunter2");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Step("start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Step("stop");
            return Task.CompletedTask;
        }
    }

    private sealed class Lifecycle(Journal journal, string name, string? failOn = null) : Recording(journal, name, failOn), IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            Step("starting");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            Step("started");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            Step("stopping");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            Step("stopped");
            return Task.CompletedTask;
        }
    }

    /// <summary>A service whose stop never ends on its own: only the stop budget ends it.</summary>
    private sealed class Stuck(Journal journal) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            journal.Add("stuck.stop");
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ServiceProvider Provider(params Func<IServiceProvider, IHostedService>[] services)
    {
        var collection = new ServiceCollection();
        foreach (var service in services)
        {
            collection.AddSingleton(service);
        }

        return collection.BuildServiceProvider();
    }

    [Fact]
    public async Task Services_start_in_registration_order_with_their_lifecycle_and_stop_newest_first()
    {
        var journal = new Journal();
        await using var provider = Provider(_ => new Lifecycle(journal, "a"), _ => new Recording(journal, "b"), _ => new Lifecycle(journal, "c"));
        var reported = new List<string>();

        var hosted = await NodeHostedServices.StartAsync(provider, reported.Add, CancellationToken.None);
        Assert.Equal(3, hosted.Started.Count);
        Assert.Equal(["a.starting", "c.starting", "a.start", "b.start", "c.start", "a.started", "c.started"], journal.All);

        await hosted.StopAsync(reported.Add);

        Assert.Equal(
            ["c.stopping", "a.stopping", "c.stop", "b.stop", "a.stop", "c.stopped", "a.stopped"],
            journal.All.Skip(7));
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_provider_with_no_hosted_service_starts_and_stops_nothing()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var reported = new List<string>();

        var hosted = await NodeHostedServices.StartAsync(provider, reported.Add, CancellationToken.None);
        await hosted.StopAsync(reported.Add);

        Assert.Empty(hosted.Started);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_service_that_fails_to_start_is_named_and_what_started_before_it_is_stopped_again()
    {
        var journal = new Journal();
        await using var provider = Provider(_ => new Recording(journal, "first"), _ => new Recording(journal, "second", failOn: "start"), _ => new Recording(journal, "third"));
        var reported = new List<string>();

        var refused = await Assert.ThrowsAsync<NodeHostedServiceException>(() => NodeHostedServices.StartAsync(provider, reported.Add, CancellationToken.None));

        Assert.Contains("failed to start", refused.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(Recording).FullName!, refused.Message, StringComparison.Ordinal);
        Assert.Contains("second cannot start", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", refused.Message, StringComparison.Ordinal);

        // The third never started, and the first is not left running.
        Assert.Equal(["first.start", "second.start", "first.stop"], journal.All);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_service_that_fails_to_stop_is_reported_and_the_others_still_stop()
    {
        var journal = new Journal();
        await using var provider = Provider(_ => new Recording(journal, "first"), _ => new Recording(journal, "second", failOn: "stop"));
        var reported = new List<string>();
        var hosted = await NodeHostedServices.StartAsync(provider, reported.Add, CancellationToken.None);

        await hosted.StopAsync(reported.Add);

        Assert.Equal(["first.start", "second.start", "second.stop", "first.stop"], journal.All);
        var line = Assert.Single(reported);
        Assert.Contains("failed to stop", line, StringComparison.Ordinal);
        Assert.Contains("second cannot stop", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stop_asked_for_while_the_services_start_stops_what_started_and_is_not_a_failure()
    {
        var journal = new Journal();
        await using var provider = Provider(_ => new Recording(journal, "first"), _ => new Cancelled(journal));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var reported = new List<string>();

        // The second service honours the token it starts with: the node stops, rather than naming it as failed.
        var stopped = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeHostedServices.StartAsync(provider, reported.Add, stop.Token));

        Assert.IsNotType<NodeHostedServiceException>(stopped);
        Assert.Equal(["first.start", "cancelled.start", "first.stop"], journal.All);
        Assert.Empty(reported);
    }

    /// <summary>A service that honours the token it starts with.</summary>
    private sealed class Cancelled(Journal journal) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            journal.Add("cancelled.start");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_service_that_does_not_stop_in_time_is_reported_and_the_node_goes_on()
    {
        var journal = new Journal();
        await using var provider = Provider(_ => new Recording(journal, "first"), _ => new Stuck(journal));
        var reported = new List<string>();
        var hosted = await NodeHostedServices.StartAsync(provider, reported.Add, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await hosted.StopAsync(reported.Add, TimeSpan.FromMilliseconds(200));
        watch.Stop();

        Assert.Equal(["first.start", "stuck.stop", "first.stop"], journal.All);
        var line = Assert.Single(reported);
        Assert.Contains("did not stop within 0.2s", line, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the stop took {watch.Elapsed}, past its budget");
    }

    [Fact]
    public void The_stop_budget_fits_inside_the_spare_time_a_node_leaves_after_its_drain()
        => Assert.InRange(NodeHostedServices.StopTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    /// <summary>
    /// The worker verb itself starts the hosted services a module registers for the node before it takes any work: a
    /// module service that fails to start refuses the node (exit 1) before a single poll, and the module's service that had
    /// started is stopped again. No control plane is reached, since nothing gets that far.
    /// </summary>
    [Fact]
    public async Task The_worker_starts_the_hosted_services_a_module_registers_for_the_node_before_it_polls()
    {
        var journal = new Journal();
        var module = new HostedModule(journal);

        var exit = await CliHost.RunAsync(["worker", "--url", "http://127.0.0.1:9", "--token", "node-token"], module);

        Assert.Equal(1, exit);
        Assert.Equal(["node.start", "failing.start", "node.stop"], journal.All);
        Assert.Contains(CliServiceScope.Worker, module.Scopes);
    }

    private sealed class HostedModule(Journal journal) : ICliModule
    {
        public List<CliServiceScope> Scopes { get; } = [];

        public string Name => "hosted-probe";

        public IReadOnlyList<CliVerb> Verbs =>
            [new CliVerb("hosted-probe", ["sqlflow hosted-probe", "                                 Nothing"], _ => Task.FromResult(0))];

        public void ConfigureServices(CliModuleServices services)
        {
            Scopes.Add(services.Scope);

            // Registered for the node alone: a command's provider starts none, and a command never gets these.
            if (services.Scope == CliServiceScope.Worker)
            {
                services.Services.AddSingleton<IHostedService>(_ => new Recording(journal, "node"));
                services.Services.AddSingleton<IHostedService>(_ => new Recording(journal, "failing", failOn: "start"));
            }
        }
    }
}
