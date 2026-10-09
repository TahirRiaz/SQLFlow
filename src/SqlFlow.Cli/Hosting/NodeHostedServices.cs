using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Cli.Hosting;

/// <summary>
/// The hosted services registered in a <c>sqlflow worker</c> node's services (<see cref="IHostedService"/>, by SQLFlow or by
/// a module's <see cref="ICliModule.ConfigureServices"/> for <see cref="CliServiceScope.Worker"/>), started and stopped with
/// the node the way a generic host starts and stops them. A node runs from a plain service provider rather than a generic
/// host, so without this a background service a module registers there (a metrics exporter, a cache warmer) would be
/// registered and never run.
/// </summary>
/// <remarks>
/// <para>
/// Start: every <see cref="IHostedLifecycleService.StartingAsync"/>, then every <see cref="IHostedService.StartAsync"/>,
/// then every <see cref="IHostedLifecycleService.StartedAsync"/>, in registration order, once the node's services are built
/// and verified and before it takes work. A service that fails to start stops the start: the services already started are
/// stopped again, newest first, and the node refuses to start with the failing service named.
/// </para>
/// <para>
/// Stop: once the node has stopped taking work and drained what it held, every started service is stopped newest first
/// (<see cref="IHostedLifecycleService.StoppingAsync"/>, <see cref="IHostedService.StopAsync"/>,
/// <see cref="IHostedLifecycleService.StoppedAsync"/>), within <see cref="StopTimeout"/> in all. A service that fails to
/// stop is reported and the others are still stopped. The node's provider is disposed after, which disposes what the
/// services left to it.
/// </para>
/// </remarks>
internal sealed class NodeHostedServices
{
    /// <summary>
    /// How long a stopping node gives its hosted services, in all, after the drain. A node's drain window is set to end
    /// inside the orchestrator's termination grace period with time to spare, and this is part of that spare time.
    /// </summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private readonly List<IHostedService> _started;

    private NodeHostedServices(List<IHostedService> started) => _started = started;

    /// <summary>The services started, in the order they started.</summary>
    public IReadOnlyList<IHostedService> Started => _started;

    /// <summary>Starts every hosted service <paramref name="services"/> holds, in registration order.</summary>
    /// <exception cref="NodeHostedServiceException">A service failed to start; the services already started were stopped again.</exception>
    public static async Task<NodeHostedServices> StartAsync(IServiceProvider services, Action<string> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(report);
        var hosted = services.GetServices<IHostedService>().ToList();
        var started = new List<IHostedService>(hosted.Count);
        IHostedService? current = null;
        try
        {
            foreach (var service in hosted)
            {
                current = service;
                if (service is IHostedLifecycleService lifecycle)
                {
                    await lifecycle.StartingAsync(ct).ConfigureAwait(false);
                }
            }

            foreach (var service in hosted)
            {
                current = service;
                await service.StartAsync(ct).ConfigureAwait(false);
                started.Add(service);
            }

            foreach (var service in hosted)
            {
                current = service;
                if (service is IHostedLifecycleService lifecycle)
                {
                    await lifecycle.StartedAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // What started is stopped again before the node gives up, so a half-started node leaves nothing running. A
            // stop asked for while the services start is a stop, not a failure of the service that was starting.
            await new NodeHostedServices(started).StopAsync(report).ConfigureAwait(false);
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            throw new NodeHostedServiceException(
                $"hosted service {Name(current)} failed to start: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        return new NodeHostedServices(started);
    }

    /// <summary>
    /// Stops the started services newest first, within <see cref="StopTimeout"/> in all (or <paramref name="timeout"/>). A
    /// service that fails to stop, or that the time runs out on, is reported through <paramref name="report"/> by name, and
    /// the rest are still stopped.
    /// </summary>
    public async Task StopAsync(Action<string> report, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_started.Count == 0)
        {
            return;
        }

        var allowed = timeout ?? StopTimeout;
        using var budget = new CancellationTokenSource(allowed);
        var stopping = Enumerable.Reverse(_started).ToList();
        await EachAsync(stopping, s => s is IHostedLifecycleService lifecycle ? lifecycle.StoppingAsync(budget.Token) : Task.CompletedTask, "stopping", allowed, report).ConfigureAwait(false);
        await EachAsync(stopping, s => s.StopAsync(budget.Token), "stop", allowed, report).ConfigureAwait(false);
        await EachAsync(stopping, s => s is IHostedLifecycleService lifecycle ? lifecycle.StoppedAsync(budget.Token) : Task.CompletedTask, "stopped", allowed, report).ConfigureAwait(false);
    }

    private static async Task EachAsync(IReadOnlyList<IHostedService> services, Func<IHostedService, Task> step, string what, TimeSpan allowed, Action<string> report)
    {
        foreach (var service in services)
        {
            try
            {
                await step(service).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                report(string.Create(
                    CultureInfo.InvariantCulture,
                    $"hosted service {Name(service)} did not {what} within {allowed.TotalSeconds:0.#}s; the node stops without waiting for it."));
            }
            catch (Exception ex)
            {
                // A service that cannot stop cleanly is named, and every other service is still stopped.
                report($"hosted service {Name(service)} failed to {what}: {SecretHygiene.RedactedMessage(ex)}");
            }
        }
    }

    private static string Name(IHostedService? service) => service?.GetType().FullName ?? "(none)";
}

/// <summary>A hosted service of a worker node failed to start. The message names the service and why.</summary>
internal sealed class NodeHostedServiceException : InvalidOperationException
{
    public NodeHostedServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
