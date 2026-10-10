using System.Net;
using System.Net.Sockets;
using SqlFlow.Acquire.Runtime;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>
/// How a connection picks among the addresses a name resolves to (RFC 8305): families interleaved and attempts staggered,
/// so a family the network silently drops cannot spend the whole connect timeout before the one that works is tried.
/// </summary>
public sealed class ConnectRaceTests
{
    private static readonly IPAddress V6a = IPAddress.Parse("2603:1026:3000:108::7");
    private static readonly IPAddress V6b = IPAddress.Parse("2603:1027:1:108::5");
    private static readonly IPAddress V6c = IPAddress.Parse("2603:1026:3000:118::2");
    private static readonly IPAddress V4a = IPAddress.Parse("40.126.53.17");
    private static readonly IPAddress V4b = IPAddress.Parse("20.190.181.5");

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void Addresses_alternate_between_the_families_keeping_the_resolver_order_within_each()
    {
        Assert.Equal([V6a, V4a, V6b, V4b, V6c], HttpClientBuilder.Interleave([V6a, V6b, V6c, V4a, V4b]));
        Assert.Equal([V4a, V6a, V4b, V6b, V6c], HttpClientBuilder.Interleave([V4a, V4b, V6a, V6b, V6c]));
        Assert.Equal([V6a, V6b], HttpClientBuilder.Interleave([V6a, V6b]));
        Assert.Equal([V4a], HttpClientBuilder.Interleave([V4a]));
        Assert.Empty(HttpClientBuilder.Interleave([]));
    }

    [Fact]
    public async Task An_address_that_never_answers_does_not_hold_back_the_next_one()
    {
        var cancelled = new List<IPAddress>();
        var connection = await HttpClientBuilder.RaceAsync(
            "login.example.com",
            [V6a, V4a],
            (address, token) => address.Equals(V6a) ? Blackhole(address, cancelled, token) : Task.FromResult(new Connection(address)),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(V4a, connection.Address);
        Assert.False(connection.Disposed);
        await WaitUntil(() => { lock (cancelled) { return cancelled.Count == 1; } });
        lock (cancelled)
        {
            Assert.Equal([V6a], cancelled);
        }
    }

    [Fact]
    public async Task A_failed_attempt_starts_the_next_at_once()
    {
        var connection = await HttpClientBuilder.RaceAsync(
            "login.example.com",
            [V6a, V4a],
            (address, _) => address.Equals(V6a)
                ? Task.FromException<Connection>(new SocketException((int)SocketError.NetworkUnreachable))
                : Task.FromResult(new Connection(address)),
            TimeSpan.FromHours(1),
            CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(V4a, connection.Address);
    }

    [Fact]
    public async Task Every_address_failing_names_each_one_and_why()
    {
        var failure = await Assert.ThrowsAsync<SocketException>(() => HttpClientBuilder.RaceAsync<Connection>(
            "login.example.com",
            [V6a, V4a],
            (address, _) => Task.FromException<Connection>(new SocketException((int)(address.Equals(V6a) ? SocketError.NetworkUnreachable : SocketError.ConnectionRefused))),
            TimeSpan.FromHours(1),
            CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(SocketError.ConnectionRefused, failure.SocketErrorCode);
        Assert.StartsWith("No address of 'login.example.com' accepted a connection: 2603:1026:3000:108::7 (", failure.Message, StringComparison.Ordinal);
        Assert.Contains("; 40.126.53.17 (", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_connect_timeout_cancels_every_attempt_still_running()
    {
        var cancelled = new List<IPAddress>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HttpClientBuilder.RaceAsync(
            "login.example.com",
            [V6a, V4a],
            (address, token) => Blackhole(address, cancelled, token),
            TimeSpan.FromMilliseconds(20),
            timeout.Token).WaitAsync(Patience));

        await WaitUntil(() => { lock (cancelled) { return cancelled.Count == 2; } });
        lock (cancelled)
        {
            Assert.Equal([V6a, V4a], cancelled.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork));
        }
    }

    [Fact]
    public async Task A_losing_attempt_that_connects_after_the_winner_is_closed()
    {
        var late = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = await HttpClientBuilder.RaceAsync(
            "login.example.com",
            [V6a, V4a],
            (address, _) => address.Equals(V6a) ? late.Task : Task.FromResult(new Connection(address)),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None).WaitAsync(Patience);

        var loser = new Connection(V6a);
        late.SetResult(loser);

        await WaitUntil(() => loser.Disposed);
        Assert.Equal(V4a, connection.Address);
        Assert.False(connection.Disposed);
    }

    private static async Task<Connection> Blackhole(IPAddress address, List<IPAddress> cancelled, CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            lock (cancelled)
            {
                cancelled.Add(address);
            }

            throw;
        }

        throw new InvalidOperationException("An infinite delay ended without being cancelled.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Connection(IPAddress address) : IDisposable
    {
        public IPAddress Address { get; } = address;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
