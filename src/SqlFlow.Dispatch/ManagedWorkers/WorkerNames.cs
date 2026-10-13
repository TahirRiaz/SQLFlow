using System.Security.Cryptography;

namespace SqlFlow.Dispatch.ManagedWorkers;

/// <summary>
/// The names managed workers poll under: <c>{prefix}-{pool part}-{build tag}{6 hex}</c>, for example
/// <c>worker-default-3f9a0c1d2e</c>. The pool part names the pool so a worker can be placed in its pool before it has
/// ever polled (a launcher's list carries only names); the build tag (4 hex) is the starting control plane's build, so a
/// later control plane tells its own build's workers from another's; and the random tail keeps every launch unique.
/// Every name is lowercase letters, digits and '-', at most 56 characters, so it is valid as a node name, a Kubernetes
/// label value and a platform job id.
/// </summary>
public static class WorkerNames
{
    /// <summary>The prefix used when none is configured.</summary>
    public const string DefaultPrefix = "worker";

    /// <summary>The pool part of the default pool's workers.</summary>
    public const string DefaultPoolPart = "default";

    /// <summary>The build tag of a control plane that could not read its own build.</summary>
    public const string UnknownBuild = "0000";

    private const int BuildTagLength = 4;

    private const int MaxPrefixLength = 20;
    private const int MaxPoolPartLength = 24;
    private const int SuffixLength = 10;

    /// <summary>The pool part of a pool's worker names: lowercase letters and digits, every other run of characters
    /// one '-', trimmed, at most 24 characters. The default pool is <see cref="DefaultPoolPart"/>.</summary>
    public static string PoolPart(string poolKey)
    {
        ArgumentNullException.ThrowIfNull(poolKey);
        if (poolKey.Length == 0)
        {
            return DefaultPoolPart;
        }

        var chars = new List<char>(poolKey.Length);
        foreach (var c in poolKey.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                chars.Add(c);
            }
            else if (chars.Count > 0 && chars[^1] != '-')
            {
                chars.Add('-');
            }
        }

        var part = new string([.. chars]).Trim('-');
        if (part.Length > MaxPoolPartLength)
        {
            part = part[..MaxPoolPartLength].TrimEnd('-');
        }

        return part.Length == 0 ? "pool" : part;
    }

    /// <summary>A new, unique worker name for a pool, started by a control plane of build <paramref name="buildTag"/>.</summary>
    public static string New(string prefix, string poolKey, string buildTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ValidateBuildTag(buildTag, nameof(buildTag));
        var random = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes((SuffixLength - BuildTagLength) / 2));
        return $"{prefix}-{PoolPart(poolKey)}-{buildTag}{random}";
    }

    /// <summary>The build tag in a managed worker's name; null when the name is not one the fleet gives.</summary>
    public static string? BuildTagOf(string name, string prefix)
        => PoolPartOf(name, prefix) is null ? null : name[^SuffixLength..][..BuildTagLength];

    /// <summary>Refuses a build tag that is not four lowercase hex digits.</summary>
    public static void ValidateBuildTag(string? tag, string setting)
    {
        if (tag is null || tag.Length != BuildTagLength || tag.Any(c => !char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)))
        {
            throw new InvalidOperationException($"{setting} must be {BuildTagLength} lowercase hex digits; '{tag}' is not.");
        }
    }

    /// <summary>The pool part of a managed worker's name, or null when the name is not one the fleet with
    /// <paramref name="prefix"/> gives (a worker started by hand, the control plane's own node).</summary>
    public static string? PoolPartOf(string name, string prefix)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var lead = prefix + "-";
        if (!name.StartsWith(lead, StringComparison.Ordinal) || name.Length < lead.Length + 1 + 1 + SuffixLength)
        {
            return null;
        }

        var dash = name.Length - SuffixLength - 1;
        if (name[dash] != '-')
        {
            return null;
        }

        for (var i = dash + 1; i < name.Length; i++)
        {
            if (!char.IsAsciiHexDigitLower(name[i]) && !char.IsAsciiDigit(name[i]))
            {
                return null;
            }
        }

        var part = name[lead.Length..dash];
        if (part.Length == 0 || part.Length > MaxPoolPartLength || part[0] == '-' || part[^1] == '-')
        {
            return null;
        }

        foreach (var c in part)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return null;
            }
        }

        return part;
    }

    /// <summary>Refuses a prefix that would not make valid names: 1 to 20 lowercase letters, digits and '-', starting
    /// with a letter and not ending with '-'.</summary>
    public static void ValidatePrefix(string? prefix, string setting)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > MaxPrefixLength || !char.IsAsciiLetterLower(prefix[0])
            || prefix[^1] == '-' || prefix.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
        {
            throw new InvalidOperationException(
                $"{setting} must be 1 to {MaxPrefixLength} lowercase letters, digits and '-', starting with a letter and not ending with '-'; '{prefix}' is not.");
        }
    }

    /// <summary>Refuses pools whose worker names could not be told apart: two pools with the same pool part, or a named
    /// pool whose part is the default pool's.</summary>
    public static void ValidatePools(IEnumerable<string> poolKeys, string setting)
    {
        ArgumentNullException.ThrowIfNull(poolKeys);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in poolKeys)
        {
            var part = PoolPart(key);
            if (seen.TryGetValue(part, out var other))
            {
                var first = other.Length == 0 ? "the default pool" : $"'{other}'";
                var second = key.Length == 0 ? "the default pool" : $"'{key}'";
                throw new InvalidOperationException(
                    $"{setting}: {first} and {second} would both name their workers '{part}'; rename one of the pools.");
            }

            seen[part] = key;
        }
    }
}
