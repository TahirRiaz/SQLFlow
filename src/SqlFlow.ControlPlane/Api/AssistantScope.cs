using Microsoft.AspNetCore.Http;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The AI assistant surface: requests the GUI chat and Slack assistants make through the SQLFlow MCP server. Both
/// gateways point the MCP connector at <c>?surface=assistant</c>, and the MCP server marks every control-plane call
/// of such a session with <see cref="HeaderName"/>. The caller's own bearer still decides what it may do; the header
/// only narrows, so a request that omits it gets nothing more than its token already allows. The model cannot set the
/// header, which is the property that matters: it cannot opt out of the narrowing. A marked call is held to the routes
/// opened to assistant runs (<c>AssistantDelegationMiddleware</c>), whatever credential it carries.
/// <para>
/// What the marker narrows is data, never schema. Schema (table and column names, definitions, flow YAML, executed
/// statements) is not sensitive and is served to the assistant as to anyone else. A data-operations task the
/// assistant enqueues reads live rows, so it must read nothing a query from the assistant could not: it passes the
/// read-only and column guards first (<see cref="ColumnPolicyGuard.EnsureTaskAllowedAsync"/>).
/// </para>
/// </summary>
public static class AssistantScope
{
    /// <summary>The request header marking the assistant surface.</summary>
    public const string HeaderName = "X-SqlFlow-Surface";

    /// <summary>The <see cref="HeaderName"/> value of the assistant surface.</summary>
    public const string AssistantValue = "assistant";

    /// <summary>Whether <paramref name="http"/> carries the assistant surface marker.</summary>
    public static bool IsAssistant(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Headers.TryGetValue(HeaderName, out var values)
            && values.Any(v => string.Equals(v?.Trim(), AssistantValue, StringComparison.OrdinalIgnoreCase));
    }
}
