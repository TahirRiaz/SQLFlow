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
    /// failure (refused, unreachable, timed out) from the last candidate rather than a socket-lifecycle artifact.
    /// </summary>
    private static async ValueTask<Stream> KeepAliveConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);

        for (var i = 0; i < addresses.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var socket = CreateKeepAliveSocket(addresses[i].AddressFamily);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[i], endPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) when (i < addresses.Length - 1)
            {
                // A further candidate remains, so this address's failure is not terminal: drop the (now unusable)
                // socket and let the next iteration start a clean one. The final candidate's exception is allowed
                // to propagate instead, carrying the genuine reason the endpoint could not be reached.
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // Resolution returned nothing at all, so there was never an address to try.
        throw new SocketException((int)SocketError.HostNotFound);
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
