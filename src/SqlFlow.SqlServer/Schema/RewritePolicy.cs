namespace SqlFlow.SqlServer.Schema;

/// <summary>How a table-rewrite change should be handled.</summary>
public enum RewriteHandling
{
    /// <summary>Apply the rewrite inline (the flow opted in).</summary>
    Inline,

    /// <summary>Refuse the rewrite: it would hold a long blocking lock and the flow has not opted in.</summary>
    RequireOptIn,
}

/// <summary>
/// The pure policy gate for an expensive table rewrite. A rewrite is allowed inline only when the flow has
/// explicitly opted in (Schema sync's AllowTableRewrite); otherwise it is refused so it never silently holds
/// a long schema-modification lock.
/// </summary>
public static class RewritePolicy
{
    public static RewriteHandling Decide(bool allowTableRewrite)
        => allowTableRewrite ? RewriteHandling.Inline : RewriteHandling.RequireOptIn;
}
