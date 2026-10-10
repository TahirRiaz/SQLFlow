using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime;

/// <summary>Builds the per-flow <see cref="HttpClient"/>: the configured timeout, TLS verification toggle, a bounded
/// redirect policy, and a stable user agent. One client per run (its handler carries the flow's TLS/timeout).</summary>
public static class HttpClientBuilder
{
    private static readonly string UserAgent = $"SqlFlow-Acquire/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"}";

    public static HttpClient Build(AcquireReliability reliability)
    {
        ArgumentNullException.ThrowIfNull(reliability);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(reliability.TimeoutSeconds, 30)),
            // HTTP/2 keep-alive pings: on a long, rate-limited fan-out (e.g. a per-item backfill spanning
            // hours) a multiplexed connection can sit idle between requests, and an intermediary (NAT, load
            // balancer, corporate proxy) silently reaps idle connections. Pinging keeps the connection
            // provably alive so the next request reuses it instead of discovering a half-open socket the hard
            // way (a reset mid-request). Applies to negotiated HTTP/2 only.
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            // HTTP/1.1 keep-alive: most REST endpoints negotiate 1.1, where the HTTP/2 pings above do not
            // apply, so enable TCP-level keepalive probes on every connected socket for the same purpose.
            ConnectCallback = KeepAliveConnectAsync,
        };

        if (!reliability.VerifyTls)
        {
            // Deliberate opt-in, gated behind the flow's explicit VerifyTls: false, for targets with a self-signed
            // or enterprise-internal certificate. Accepting any certificate here is the requested behavior, not a
            // lapse, so CA5359 is suppressed for this one assignment.
#pragma warning disable CA5359
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(reliability.TimeoutSeconds),
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    /// <summary>
    /// Opens each new pooled connection on a socket with TCP keepalive enabled, so an idle HTTP/1.1 connection on a
    /// long-running flow is kept provably alive (and a genuinely dead one is detected by the OS) rather than being
    /// silently dropped by an intermediary and surfacing as a reset on the next request. The handler's
    /// <see cref="SocketsHttpHandler.ConnectTimeout"/> is honored through <paramref name="ct"/>, which the handler
    /// signals when the connect deadline elapses. Keepalive tuning (30s idle before the first probe, 15s between
    /// probes, up to 4 unanswered probes before the OS tears the connection down) is deliberately well inside the
    /// two-minute pooled-connection lifetime, and is supported on both Windows and the Linux worker containers.
    ///
    /// The host is resolved here and each candidate address is attempted on its OWN socket, created for that
    /// address's family. Handing a multi-address <see cref="DnsEndPoint"/> to a single socket instead is what broke
    /// every acquisition on Linux: the two-argument <see cref="Socket"/> constructor yields a dual-stack IPv6 socket,
    /// a host with no usable IPv6 route fails the first attempt instantly, and a socket that has failed a connect
    /// cannot be reused on Linux, so the fallback to the next address threw "Sockets on this platform are invalid for
    /// use after a failed connection attempt" and masked the real error. Windows hides the bug because ConnectEx
    /// permits the retry. One socket per address is correct on both, and it means the caller sees the actual connect
    /// failures (refused, unreachable, timed out) rather than a socket-lifecycle artifact.
    ///
    /// The addresses are raced the Happy Eyeballs way (RFC 8305): families interleaved, each attempt given
    /// <see cref="ConnectionAttemptDelay"/> alone before the next starts beside it, so an address family the network
    /// silently drops (IPv6 assigned but not routed) cannot spend the whole connect timeout before the one that works is
    /// tried.
    /// </summary>
    private static async ValueTask<Stream> KeepAliveConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var resolved = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);

        var socket = await RaceAsync(
            endPoint.Host, Interleave(resolved), (address, token) => ConnectAsync(address, endPoint.Port, token),
            ConnectionAttemptDelay, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>How long a connection attempt runs alone before the next address is tried beside it (RFC 8305, section 5).</summary>
    internal static readonly TimeSpan ConnectionAttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Orders <paramref name="addresses"/> alternating between the family of the first one and the other (RFC 8305,
    /// section 4), keeping the resolver's order within each family.
    /// </summary>
    internal static IPAddress[] Interleave(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count < 2)
        {
            return [.. addresses];
        }

        var family = addresses[0].AddressFamily;
        var preferred = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == family));
        var other = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily != family));
        var ordered = new IPAddress[addresses.Count];
        for (var i = 0; i < ordered.Length; i++)
        {
            ordered[i] = (i % 2 == 0 && preferred.Count > 0) || other.Count == 0 ? preferred.Dequeue() : other.Dequeue();
        }

        return ordered;
    }

    /// <summary>
    /// Starts an attempt on the first address, and one on the next each time <paramref name="attemptDelay"/> passes or an
    /// attempt fails, until one connects; the others are cancelled and anything they still open is disposed. Every
    /// address failing is a <see cref="SocketException"/> naming each one and why; <paramref name="ct"/> (the handler's
    /// connect timeout) cancels the whole race.
    /// </summary>
    internal static async Task<T> RaceAsync<T>(
        string host, IReadOnlyList<IPAddress> addresses, Func<IPAddress, CancellationToken, Task<T>> attempt, TimeSpan attemptDelay,
        CancellationToken ct)
        where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(attempt);
        if (addresses.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound, $"'{host}' resolves to no address.");
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new Dictionary<Task<T>, IPAddress>();
        var failures = new List<string>(addresses.Count);
        var lastError = SocketError.HostUnreachable;
        var next = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (next < addresses.Count)
                {
                    var address = addresses[next++];
                    running.Add(attempt(address, race.Token), address);
                }

                if (running.Count == 0)
                {
                    break;
                }

                var waitingOn = running.Keys.Cast<Task>();
                if (next < addresses.Count)
                {
                    waitingOn = waitingOn.Append(Task.Delay(attemptDelay, race.Token));
                }

                if (await Task.WhenAny(waitingOn).ConfigureAwait(false) is not Task<T> finished || !running.Remove(finished, out var tried))
                {
                    continue;
                }

                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    return finished.Result;
                }

                ct.ThrowIfCancellationRequested();
                var error = finished.Exception?.GetBaseException();
                if (error is SocketException socketError)
                {
                    lastError = socketError.SocketErrorCode;
                }

                failures.Add($"{tried} ({error?.Message ?? "cancelled"})");
            }
        }
        finally
        {
            await race.CancelAsync().ConfigureAwait(false);
            foreach (var loser in running.Keys)
            {
                // A losing attempt can still connect after the winner did; its connection is closed rather than leaked.
                _ = loser.ContinueWith(
                    static t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion)
                        {
                            t.Result.Dispose();
                        }
                        else
                        {
                            _ = t.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        throw new SocketException((int)lastError, $"No address of '{host}' accepted a connection: {string.Join("; ", failures)}.");
    }

    /// <summary>One attempt: a keepalive socket of <paramref name="address"/>'s family connected to it, or disposed when
    /// the connect fails.</summary>
    private static async Task<Socket> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = CreateKeepAliveSocket(address.AddressFamily);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Creates a TCP socket for <paramref name="family"/> with the keepalive probe policy described on
    /// <see cref="KeepAliveConnectAsync"/>. The family comes from the resolved address so an IPv4-only host is
    /// dialled on an IPv4 socket rather than through a dual-stack mapping the platform may not route.</summary>
    private static Socket CreateKeepAliveSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 15);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        return socket;
    }
}
