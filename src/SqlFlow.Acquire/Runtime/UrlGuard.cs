using System.Net;
using System.Net.Sockets;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// SSRF guard for outbound acquisition requests. A URL must be <c>http</c>/<c>https</c> with a host; a literal-IP
/// host in a private, loopback, link-local (incl. the cloud metadata address 169.254.169.254), unspecified,
/// multicast, or unique-local range is always rejected; and when an allowlist is configured the host must match it
/// (<c>*.suffix</c> matches the bare domain and any subdomain, anything else is an exact host match).
/// </summary>
public sealed class UrlGuard
{
    private readonly IReadOnlyList<string> _allowlist;

    public UrlGuard(IReadOnlyList<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        _allowlist = allowlist;
    }

    public void Check(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Scheme is not ("http" or "https"))
        {
            throw new SqlFlowException($"URL '{url}' uses scheme '{url.Scheme}'; only http and https are allowed.");
        }

        var host = url.Host;
        if (string.IsNullOrEmpty(host))
        {
            throw new SqlFlowException($"URL '{url}' has no host.");
        }

        if (IPAddress.TryParse(host, out var ip) && IsDangerous(ip))
        {
            throw new SqlFlowException($"URL '{url}' targets a blocked address range ({ip}). Private, loopback, link-local, and metadata addresses are not reachable.");
        }

        if (_allowlist.Count > 0 && !IsAllowed(host))
        {
            throw new SqlFlowException(
                $"Host '{host}' is not in the source url allowlist ({string.Join(", ", _allowlist)}). " +
                "Add it to reliability.urlAllowlist to permit the request.");
        }
    }

    private bool IsAllowed(string host)
    {
        foreach (var pattern in _allowlist)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[2..];
                if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDangerous(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                          // 0.0.0.0/8 unspecified
                10 => true,                         // 10.0.0.0/8 private
                127 => true,                        // loopback (also caught above)
                169 when b[1] == 254 => true,       // 169.254.0.0/16 link-local (incl. 169.254.169.254 metadata)
                172 when b[1] >= 16 && b[1] <= 31 => true, // 172.16.0.0/12 private
                192 when b[1] == 168 => true,       // 192.168.0.0/16 private
                >= 224 => true,                     // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            {
                return true;
            }

            // Unique-local addresses fc00::/7 (first byte 0xFC or 0xFD).
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // IPv4-mapped IPv6 (::ffff:a.b.c.d) must be judged on the embedded v4 address.
            if (ip.IsIPv4MappedToIPv6 && IsDangerous(ip.MapToIPv4()))
            {
                return true;
            }
        }

        return false;
    }
}
