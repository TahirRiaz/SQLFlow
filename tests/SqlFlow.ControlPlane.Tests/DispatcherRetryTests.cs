using SqlFlow.Dispatch;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The retry policy behind a node's calls to the dispatcher (<see cref="DispatcherRetry"/>): a refusal from a
/// replica that does not own dispatch, or an unreachable control plane, is repeated after the configured waits
/// and then delivered; a request the dispatcher rejects is thrown at once; the last failure surfaces once the
/// waits are spent; and cancellation ends the waiting. This is what turns a control plane behind a load balancer,
/// where only one replica owns dispatch, from a source of lost outcomes into something a node never notices.
/// </summary>
public sealed class DispatcherRetryTests
{
    private static readonly TimeSpan[] Fast = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];

    [Fact]
    public async Task RetriesARefusal_ThenReturnsTheAnswer()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();

        var answer = await DispatcherRetry.RunAsync(
            Fast,
            _ =>
            {
                calls++;
                return calls switch
                {
                    1 => throw new NodeTransportException("the control plane answered /poll with 503", 503, retryable: true),
                    2 => throw new DispatchInactiveException("this replica does not own dispatch"),
                    _ => Task.FromResult(42),
                };
            },
            (_, wait) => waits.Add(wait),
            CancellationToken.None);

        Assert.Equal(42, answer);
        Assert.Equal(3, calls);
        Assert.Equal(Fast, waits);
    }

    [Fact]
    public async Task ThrowsARejectedRequest_AtOnce()
    {
        var calls = 0;
        var retried = false;

        var error = await Assert.ThrowsAsync<NodeTransportException>(() => DispatcherRetry.RunAsync<int>(
            Fast,
            _ =>
            {
                calls++;
                throw new NodeTransportException("the control plane answered /outcome with 400", 400, retryable: false);
            },
            (_, _) => retried = true,
            CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(1, calls);
        Assert.False(retried);
    }

    [Fact]
    public async Task GivesUpAfterTheWaits_ThrowingTheLastFailure()
    {
        var calls = 0;

        var error = await Assert.ThrowsAsync<NodeTransportException>(() => DispatcherRetry.RunAsync<int>(
            Fast,
            _ =>
            {
                calls++;
                throw new NodeTransportException($"attempt {calls} refused", 503, retryable: true);
            },
            (_, _) => { },
            CancellationToken.None));

        Assert.Equal("attempt 3 refused", error.Message);
        Assert.Equal(Fast.Length + 1, calls);
    }

    [Fact]
    public async Task StopsWaiting_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DispatcherRetry.RunAsync<int>(
            [TimeSpan.FromSeconds(30)],
            _ =>
            {
                calls++;
                throw new NodeTransportException("unreachable", null, retryable: true);
            },
            (_, _) => cts.Cancel(),
            cts.Token));

        Assert.Equal(1, calls);
    }
}
