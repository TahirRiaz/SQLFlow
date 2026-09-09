using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// Turns a subscription's declarative filters (the kind list and the optional flow-name wildcard pattern) into a
/// predicate over events. Patterns are comma-separated globs (<c>*</c> = any run of characters, <c>?</c> = one),
/// matched case-insensitively against the full flow name; everything else in the pattern is literal, so a flow
/// name containing regex metacharacters can never break or widen the match.
/// </summary>
public static class NotificationSubscriptionFilter
{
    /// <summary>A hard ceiling on pattern evaluation, so a pathological pattern can never stall a dispatch tick
    /// (the translated glob is linear, but the timeout makes that a guarantee rather than an argument).</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The predicate for one subscription. Built once per dispatch (the regex is compiled per call, and
    /// one message evaluates it against at most a few hundred events).</summary>
    public static Func<CatalogNotificationEvent, bool> Build(CatalogNotificationSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var kinds = ParseKinds(subscription.Kinds);
        var pattern = BuildFlowPattern(subscription.FlowPattern);
        return e => kinds.Contains(e.Kind) && (pattern is null || SafeIsMatch(pattern, e.FlowName));
    }

    /// <summary>The subscription's kind set (comma-separated storage form to a set).</summary>
    public static HashSet<string> ParseKinds(string kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        return kinds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Translates a comma-separated glob list to one anchored, case-insensitive regex; null for a blank
    /// pattern (= every flow). Throws <see cref="ArgumentException"/> for a pattern that produces no usable glob
    /// (for example only commas), so the API can reject it at subscription time instead of silently matching nothing.</summary>
    public static Regex? BuildFlowPattern(string? flowPattern)
    {
        if (string.IsNullOrWhiteSpace(flowPattern))
        {
            return null;
        }

        var globs = flowPattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (globs.Length == 0)
        {
            throw new ArgumentException("The flow pattern contains no usable glob.", nameof(flowPattern));
        }

        var builder = new StringBuilder("^(?:");
        for (var i = 0; i < globs.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('|');
            }

            foreach (var ch in globs[i])
            {
                switch (ch)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        builder.Append(Regex.Escape(ch.ToString()));
                        break;
                }
            }
        }

        builder.Append(")$");
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }

    /// <summary>A match that treats a (theoretically unreachable) regex timeout as "no match" instead of failing
    /// the whole dispatch: one odd flow name must never stop everyone's notifications.</summary>
    private static bool SafeIsMatch(Regex pattern, string flowName)
    {
        try
        {
            return pattern.IsMatch(flowName);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
