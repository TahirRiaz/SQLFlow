using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// In-memory store for the OAuth 2.0 device-authorization grant (RFC 8628) used by the SQLFlow MCP server to sign
/// in without a browser callback. A device-authorization request mints a <c>device_code</c> (polled by the MCP
/// server) and a short <c>user_code</c> (typed by a human into the SQLFlow GUI's approval page). Approval binds the
/// approving user's identity and scopes to the entry; the next poll then mints a normal HS256 token for that user.
///
/// Entries live only in process memory: a control-plane restart abandons in-flight device flows, which is correct
/// for a short-lived (minutes) grant. Expired entries are pruned lazily on access and on create.
/// </summary>
public sealed class DeviceCodeStore
{
    /// <summary>Status of a device-authorization entry.</summary>
    public enum DeviceStatus
    {
        Pending,
        Approved,
        Denied,
    }

    public sealed class Entry
    {
        public required string DeviceCode { get; init; }
        public required string UserCode { get; init; }
        public required IReadOnlyList<string> RequestedScopes { get; init; }
        public required DateTime ExpiresUtc { get; init; }
        public required int IntervalSeconds { get; init; }

        public DeviceStatus Status { get; set; } = DeviceStatus.Pending;
        public DateTime? LastPolledUtc { get; set; }

        // Populated on approval.
        public string? Username { get; set; }
        public IReadOnlyList<string>? GrantedScopes { get; set; }
        public string? Role { get; set; }
        public Guid? UserId { get; set; }
    }

    // Characters excluding easily-confused 0/O/1/I so a human can retype the code reliably.
    private const string UserCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Entry> _byDeviceCode = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _userCodeToDevice = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Create a pending entry and return it. <paramref name="ttlSeconds"/> bounds how long the codes are
    /// valid; <paramref name="intervalSeconds"/> is the minimum poll cadence advertised to the client.</summary>
    public Entry Create(IReadOnlyList<string> requestedScopes, DateTime nowUtc, int ttlSeconds, int intervalSeconds)
    {
        Prune(nowUtc);

        // Retry generation on the astronomically-unlikely collision.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var deviceCode = RandomHex(32);
            var userCode = NewUserCode();
            var entry = new Entry
            {
                DeviceCode = deviceCode,
                UserCode = userCode,
                RequestedScopes = requestedScopes,
                ExpiresUtc = nowUtc.AddSeconds(ttlSeconds),
                IntervalSeconds = intervalSeconds,
            };
            if (_byDeviceCode.TryAdd(deviceCode, entry)
                && _userCodeToDevice.TryAdd(NormalizeUserCode(userCode), deviceCode))
            {
                return entry;
            }

            // Roll back a partial insert before retrying.
            _byDeviceCode.TryRemove(deviceCode, out _);
        }

        throw new InvalidOperationException("Could not allocate a unique device code.");
    }

    /// <summary>Look up a live entry by device code, pruning it if expired.</summary>
    public Entry? FindByDeviceCode(string deviceCode, DateTime nowUtc)
    {
        if (!_byDeviceCode.TryGetValue(deviceCode, out var entry))
        {
            return null;
        }

        if (nowUtc >= entry.ExpiresUtc)
        {
            Remove(entry);
            return null;
        }

        return entry;
    }

    /// <summary>Look up a live entry by the human-typed user code, pruning it if expired.</summary>
    public Entry? FindByUserCode(string userCode, DateTime nowUtc)
    {
        if (!_userCodeToDevice.TryGetValue(NormalizeUserCode(userCode), out var deviceCode))
        {
            return null;
        }

        return FindByDeviceCode(deviceCode, nowUtc);
    }

    /// <summary>Remove an entry (called after a token is minted or on denial).</summary>
    public void Remove(Entry entry)
    {
        _byDeviceCode.TryRemove(entry.DeviceCode, out _);
        _userCodeToDevice.TryRemove(NormalizeUserCode(entry.UserCode), out _);
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var kvp in _byDeviceCode)
        {
            if (nowUtc >= kvp.Value.ExpiresUtc)
            {
                Remove(kvp.Value);
            }
        }
    }

    private static string NormalizeUserCode(string code)
        => code.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static string NewUserCode()
    {
        Span<char> buffer = stackalloc char[8];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        return $"{new string(buffer[..4])}-{new string(buffer[4..])}";
    }

    private static string RandomHex(int bytes)
    {
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }
}
