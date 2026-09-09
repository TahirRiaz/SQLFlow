using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;
using SqlFlow.SourceControl.Proposals;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One preflight finding, tied to the proposed file it is about.</summary>
public sealed record ProposalFinding(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>The preflight verdict: <see cref="Errors"/> block the proposal (the file would never import, so
/// merging it lands nothing); <see cref="Warnings"/> ride along into the response and the pull-request body so
/// the human reviewer sees them.</summary>
public sealed record ProposalPreflightResult(
    IReadOnlyList<ProposalFinding> Errors, IReadOnlyList<ProposalFinding> Warnings);

/// <summary>
/// Validates a proposal's files BEFORE any branch is pushed, with the exact code the estate runs afterwards:
/// every YAML flow file goes through the same <see cref="YamlDocumentLoader"/> the managed sync parses with, and
/// library files go through their own loaders, so "preflight passed" means "the sync will import this". Beyond
/// parseability it guards the two silent failure modes of an authored proposal: a flow that would never land
/// (unparseable, or a <c>.yml</c> extension the estate scan does not discover), and a revision that quietly
/// changes an existing flow's declared source/target endpoints, which is a design decision the reviewer must
/// see, never a side effect (endpoints are compared through <see cref="FlowDeclaredEndpoints"/> against the
/// catalog's current copy of the flow).
/// </summary>
public static class FlowProposalPreflight
{
    /// <summary>The catalog's view of one already-synced pipeline in the target repo, as the preflight needs it:
    /// the flow name, where its document lives, and the stored (secret-redacted) YAML to diff endpoints against.
    /// Only ACTIVE pipelines participate: a flow that already left git constrains nothing.</summary>
    public sealed record ExistingPipeline(string Name, string RelativePath, string Yaml);

    private static readonly YamlDocumentLoader Documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
        new YamlTranslateFlowLoader());

    private static readonly YamlScheduleLibraryLoader ScheduleLibraries = new();

    private static readonly YamlSubscriberLibraryLoader SubscriberLibraries = new();

    public static ProposalPreflightResult Run(
        IReadOnlyList<ProposalFile> files, IReadOnlyList<ExistingPipeline> existing)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(existing);

        var errors = new List<ProposalFinding>();
        var warnings = new List<ProposalFinding>();

        var existingByName = new Dictionary<string, ExistingPipeline>(StringComparer.OrdinalIgnoreCase);
        foreach (var pipeline in existing)
        {
            existingByName.TryAdd(pipeline.Name, pipeline);
        }

        // Flow names already claimed by files of THIS proposal, so an intra-proposal duplicate is caught even
        // when neither file is in the catalog yet.
        var proposedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!IsYaml(file.Path))
            {
                continue; // companion .sql/.json/.md files carry no flow contract to preflight.
            }

            if (FlowSetCollector.IsScheduleLibraryFile(file.Path))
            {
                foreach (var warning in ScheduleLibraries.Parse(file.Content, file.Path).Warnings)
                {
                    warnings.Add(new ProposalFinding(file.Path, warning));
                }

                continue;
            }

            if (FlowSetCollector.IsSubscriberLibraryFile(file.Path))
            {
                foreach (var warning in SubscriberLibraries.Parse(file.Content, file.Path).Warnings)
                {
                    warnings.Add(new ProposalFinding(file.Path, warning));
                }

                continue;
            }

            FlowDocument document;
            try
            {
                document = Documents.Parse(file.Content, file.Path);
            }
            catch (SqlFlowException ex)
            {
                errors.Add(new ProposalFinding(
                    file.Path,
                    "does not parse as a flow document, so the managed sync would silently ignore it and the "
                    + $"flow would never land: {SecretHygiene.RedactedMessage(ex)}"));
                continue;
            }

            // The estate scan discovers *.yaml only; a valid flow under .yml merges cleanly and then never
            // imports, which is the worst kind of green build.
            if (file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProposalFinding(
                    file.Path,
                    "parses as a flow document but uses the '.yml' extension; the managed sync discovers only "
                    + "'*.yaml' files, so this flow would never import. Rename the file to end in '.yaml'."));
                continue;
            }

            var headers = FlowDocumentHeaders.Project(document);
            foreach (var header in headers)
            {
                if (proposedNames.TryGetValue(header.Name, out var otherFile)
                    && !PathsEqual(otherFile, file.Path))
                {
                    warnings.Add(new ProposalFinding(
                        file.Path,
                        $"declares flow '{header.Name}', which '{otherFile}' in this same proposal also declares; "
                        + "duplicate flow names merge under one pipeline at sync, which is almost never intended."));
                }
                else
                {
                    proposedNames.TryAdd(header.Name, file.Path);
                }

                if (!existingByName.TryGetValue(header.Name, out var current))
                {
                    continue;
                }

                if (!PathsEqual(current.RelativePath, file.Path))
                {
                    warnings.Add(new ProposalFinding(
                        file.Path,
                        $"declares flow '{header.Name}', which is already declared by '{current.RelativePath}' in "
                        + "this repo; two documents with one flow name merge under one pipeline at sync, which is "
                        + "almost never intended. Revise the existing file, or rename the new flow."));
                    continue;
                }

                // Same flow, same file: this is a revision. Diff the declared endpoints against the catalog's
                // current copy so a repoint is always a visible decision in the pull request.
                if (header != headers[0])
                {
                    continue; // endpoint identity belongs to the document's primary flow.
                }

                var (removed, added) = DiffEndpoints(current, document, file.Path, warnings);
                if (removed.Count > 0 || added.Count > 0)
                {
                    warnings.Add(new ProposalFinding(
                        file.Path,
                        $"revises flow '{header.Name}' and CHANGES its declared endpoints:"
                        + Render("removed", removed) + Render("added", added)
                        + ". A flow's source and target locations are the design; confirm this repoint is "
                        + "explicitly intended and say so in the pull request, or restore the original endpoints."));
                }
            }
        }

        return new ProposalPreflightResult(errors, warnings);
    }

    /// <summary>The endpoint multiset difference between the catalog's stored copy of the flow and the proposed
    /// revision. A stored document that no longer parses cannot be diffed; that is reported as its own warning
    /// (never an error: the proposal may be the very fix) and the diff is empty.</summary>
    private static (List<string> Removed, List<string> Added) DiffEndpoints(
        ExistingPipeline current, FlowDocument proposed, string path, List<ProposalFinding> warnings)
    {
        FlowDocument currentDocument;
        try
        {
            currentDocument = Documents.Parse(current.Yaml, current.RelativePath);
        }
        catch (SqlFlowException ex)
        {
            warnings.Add(new ProposalFinding(
                path,
                "revises a flow whose stored catalog copy no longer parses, so its endpoints could not be "
                + $"compared: {SecretHygiene.RedactedMessage(ex)}"));
            return ([], []);
        }

        var before = FlowDeclaredEndpoints.Describe(currentDocument).Select(e => e.ToString()).ToList();
        var after = FlowDeclaredEndpoints.Describe(proposed).Select(e => e.ToString()).ToList();
        return (MultisetExcept(before, after), MultisetExcept(after, before));
    }

    /// <summary>Multiset difference (first minus second), order-preserving: two identical copy steps count
    /// twice, so dropping one of them is still a change.</summary>
    private static List<string> MultisetExcept(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in second)
        {
            counts[value] = counts.TryGetValue(value, out var n) ? n + 1 : 1;
        }

        var result = new List<string>();
        foreach (var value in first)
        {
            if (counts.TryGetValue(value, out var n) && n > 0)
            {
                counts[value] = n - 1;
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static string Render(string label, IReadOnlyList<string> values)
        => values.Count == 0 ? string.Empty : $" {label} [{string.Join("; ", values)}]";

    private static bool IsYaml(string path)
        => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.Replace('\\', '/').TrimStart('/'),
            b.Replace('\\', '/').TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);
}
