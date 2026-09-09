namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// A flow's "project" is the root folder it lives under within its repo: the first segment of its repo-relative
/// path, or <see cref="Root"/> when the flow sits at the repo root. It is the label a source's pipelines share and
/// the unit the lineage graph and the pipelines list are scoped by. Derived, never stored; this is the single
/// backend definition so every endpoint agrees, and it mirrors the GUI's <c>projectOf</c>.
/// </summary>
internal static class ProjectPath
{
    /// <summary>The project value a flow at the repo root coalesces to, so it is still selectable as a group.</summary>
    public const string Root = "(root)";

    /// <summary>The project (root folder) of a repo-relative, forward-slashed path: the segment before the first
    /// slash, or <see cref="Root"/> when the path has no folder (a flow at the repo root).</summary>
    public static string Of(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        return slash > 0 ? relativePath[..slash] : Root;
    }
}
