namespace SqlFlow.Acquire.Runtime;

/// <summary>Redacts sensitive HTTP header values so credentials never reach a header sidecar, the debugger, or a
/// log. Matches the common auth/cookie headers and any <c>x-api-*</c> header.</summary>
public static class HeaderRedaction
{
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "cookie", "set-cookie", "proxy-authorization", "x-api-key", "x-auth-token",
    };

    public static bool IsSensitive(string name)
        => Sensitive.Contains(name) || name.StartsWith("x-api-", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> Redact(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            result[name] = IsSensitive(name) ? "***" : value;
        }

        return result;
    }
}
