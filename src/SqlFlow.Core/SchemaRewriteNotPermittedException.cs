namespace SqlFlow.Core;

/// <summary>
/// Thrown when a schema change requires a table rewrite (for example int to bigint) but the flow has not
/// opted in. A rewrite holds a table schema-modification lock for the full row rewrite, so it is refused by
/// default rather than run as a silent, long, blocking operation; the operator opts in or runs it in a
/// maintenance window.
/// </summary>
public sealed class SchemaRewriteNotPermittedException : SqlFlowException
{
    public SchemaRewriteNotPermittedException(string target, IReadOnlyList<string> columns)
        : base($"Schema evolution on '{target}' requires a table rewrite on column(s) {string.Join(", ", columns)} " +
               "(for example int to bigint). This holds a table lock for the full rewrite and is refused by default. " +
               "Set AllowTableRewrite to run it, ideally in a maintenance window.")
    {
        Columns = columns;
    }

    public IReadOnlyList<string> Columns { get; }
}
