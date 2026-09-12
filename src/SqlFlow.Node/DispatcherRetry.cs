using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Node;

/// <summary>
/// The retry policy behind every call a node makes to the dispatcher apart from the poll loop (which has its own
/// jittered backoff): a call refused because the replica it reached does not own dispatch, or the control plane
/// was momentarily unreachable, is repeated after a growing wait until the budget is spent. Behind a load balancer
/// with several control-plane replicas only one owns dispatch, so a refusal is routine rather than a fault, and
/// during an ownership hand-over or a restart every replica refuses for a few seconds; the waits are sized to ride
/// both out. A failure that is not retryable (the request itself was wrong) is thrown at once.
/// </summary>
internal static class DispatcherRetry
{
    /// <summary>The waits for the calls a run makes while executing (a flow version, its lineage context): about half
    /// a minute in total, long enough for a hand-over, short enough that a run never sits idle for an outage the
    /// poll loop reports anyway.</summary>
    internal static readonly TimeSpan[] SupportWaits =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15),
    ];

    /// <summary>The waits for an outcome report: about a minute in total. A run's result is the one thing a node
    /// must not lose, and its lease (renewed by the poll loop throughout) outlasts this comfortably, so the report
    /// keeps trying well past what a hand-over needs before the run is left to the lease.</summary>
    internal static readonly TimeSpan[] OutcomeWaits =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15),
    ];

    /// <summary>The jitter added to every wait, so a fleet retrying after one replica's hand-over does not stampede.</summary>
    private const int JitterMilliseconds = 500;

    /// <summary>Makes <paramref name="call"/>, repeating it after each of <paramref name="waits"/> while it fails
    /// retryably (<see cref="NodeTransportException.IsRetryable"/>); <paramref name="onRetry"/> reports each wait.
    /// The last failure is thrown once the waits are spent; a non-retryable failure is thrown at once; a cancelled
    /// <paramref name="ct"/> stops the waiting.</summary>
    public static async Task<T> RunAsync<T>(
        TimeSpan[] waits, Func<CancellationToken, Task<T>> call, Action<Exception, TimeSpan> onRetry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(waits);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(onRetry);

        for (var retry = 0; ; retry++)
        {
            try
            {
                return await call(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (retry < waits.Length && NodeTransportException.IsRetryable(ex))
            {
                var wait = waits[retry];
                onRetry(ex, wait);
                await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(0, JitterMilliseconds)), ct).ConfigureAwait(false);
            }
        }
    }
}
