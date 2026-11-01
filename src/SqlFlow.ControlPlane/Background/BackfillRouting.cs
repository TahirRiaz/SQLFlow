using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Decides what each member of a backfilled group runs with, for the two ways a group is backfilled: a schedule fire
/// and a node (flow + descendants) trigger. The window can only be re-applied downstream where a file's date survives
/// its re-landing, so the routing turns on where a file flow reads that date:
/// <list type="bullet">
/// <item>A file flow dated by the file's modified time cannot take the window below the flow that re-landed its files:
/// the re-landed files carry a fresh timestamp no past window selects. It runs at defaults, and that fresh timestamp
/// clears its watermark.</item>
/// <item>A file flow dated from the file's name or path (<c>fileDate.from: name</c> or <c>path</c>) is the opposite:
/// a re-landed file keeps its old date, which its watermark would filter out, so it takes the window wherever it sits
/// in the set. The window selects the same files it would have selected before the re-land.</item>
/// </list>
/// A relational ingestion reads from the source minimum either way, and a member left out of a returned map runs with
/// default parameters.
/// </summary>
public static class BackfillRouting
{
    /// <summary>The flow kinds a schedule backfill's window starts at when they sit in the first wave of the fired
    /// set: the integration flows that fetch from an external system (copy, acquire, sftp) and a root file flow. The
    /// silver (relational ingestion) layer is handled separately, by reprocessing from the source minimum. Every other
    /// kind (stored procedure, export, health check, inventory) is not part of a backfill and runs at defaults.</summary>
    private static readonly HashSet<string> IntegrationKinds =
        new(StringComparer.OrdinalIgnoreCase) { "cpy", "api", "sftp", "file" };

    /// <summary>
    /// Whether a flow's stored YAML is a file flow that dates its files from their name or path. A definition that no
    /// longer parses answers false: the routing does not refuse the group over it, because that member's own run fails
    /// on the same definition and reports why.
    /// </summary>
    public static bool ReadsDateFromFile(YamlDocumentLoader documents, string? yaml)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return false;
        }

        try
        {
            return documents.Parse(yaml, "<catalog>") is FileFlowDocument file
                && FileDateSpec.FromOptions(file.Flow.Source.Options) is not null;
        }
        catch (SqlFlowException)
        {
            return false;
        }
    }

    /// <summary>The names of the <paramref name="members"/> that <see cref="ReadsDateFromFile"/> holds for, read from
    /// the catalog's stored YAML, which is the definition each member's run executes.</summary>
    public static async Task<IReadOnlySet<string>> FileDatedMembersAsync(
        CatalogDbContext catalog, YamlDocumentLoader documents, Guid repoId, IReadOnlyList<RunScopeMember> members,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(members);
        var fileFlows = members
            .Where(m => string.Equals(m.FlowKind, "file", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.FlowName)
            .ToList();
        if (fileFlows.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var definitions = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && fileFlows.Contains(p.Name))
            .Select(p => new { p.Name, p.Yaml })
            .ToListAsync(ct).ConfigureAwait(false);
        return definitions
            .Where(d => ReadsDateFromFile(documents, d.Yaml))
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Routes a schedule fire's members to their backfill parameters. Unlike a node run there is no single anchor, so
    /// the routing keys off each member's ROLE in the fired set: a member of <see cref="IntegrationKinds"/> in the
    /// first wave takes the window and force-re-lands its files, so the requested slice re-enters the pipeline; a
    /// relational ingestion takes MIN-from-source; a member of <paramref name="fileDated"/> takes the window; every
    /// other member runs at defaults. "Root" is the minimum wave of the fired set: an external-source reader has
    /// nothing feeding it, so it sits in wave 0.
    /// </summary>
    public static Dictionary<string, RunParameters> ForSchedule(
        IReadOnlyList<RunScopeMember> members, RunParameters window, IReadOnlySet<string> fileDated)
    {
        ArgumentNullException.ThrowIfNull(members);
        var rootWave = members.Min(m => m.Wave);
        return Route(members, window, fileDated, m => m.Wave == rootWave && IntegrationKinds.Contains(m.FlowKind));
    }

    /// <summary>
    /// Routes a node run's members to their backfill parameters: the anchor (the flow the operator picked) takes the
    /// window, a relational descendant takes MIN-from-source, a member of <paramref name="fileDated"/> takes the
    /// window, and every other descendant runs as defined.
    /// </summary>
    public static Dictionary<string, RunParameters> ForNode(
        IReadOnlyList<RunScopeMember> members, string anchor, RunParameters window, IReadOnlySet<string> fileDated)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Route(members, window, fileDated, m => string.Equals(m.FlowName, anchor, StringComparison.Ordinal));
    }

    private static Dictionary<string, RunParameters> Route(
        IReadOnlyList<RunScopeMember> members, RunParameters window, IReadOnlySet<string> fileDated,
        Func<RunScopeMember, bool> isRoot)
    {
        var reprocess = new RunParameters { ReprocessFromSourceMin = true };
        var map = new Dictionary<string, RunParameters>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (isRoot(member) || fileDated.Contains(member.FlowName))
            {
                map[member.FlowName] = window;
            }
            else if (string.Equals(member.FlowKind, "ing", StringComparison.OrdinalIgnoreCase))
            {
                map[member.FlowName] = reprocess;
            }
        }

        return map;
    }
}
