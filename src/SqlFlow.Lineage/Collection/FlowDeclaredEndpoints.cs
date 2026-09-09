using SqlFlow.Core;
using SqlFlow.Yaml;

namespace SqlFlow.Lineage.Collection;

/// <summary>One declared endpoint of a flow document: <see cref="Role"/> says which side it is
/// (source / target / landing / delivery / procedure / remote / local / output) and <see cref="Value"/> is the
/// endpoint's identity as the document declares it (server identity plus object name, or a file location).
/// Rendered as <c>role: value</c> wherever a human reads it.</summary>
public sealed record DeclaredEndpoint(string Role, string Value)
{
    public override string ToString() => $"{Role}: {Value}";
}

/// <summary>
/// Projects the DECLARED ENDPOINTS of any flow document: where it reads from and where it writes to, exactly as
/// authored. A flow's endpoints are its design contract (what looks like a dead source can be a staged cutover),
/// so a revision that changes them must be a visible, deliberate decision; the proposal preflight compares this
/// projection for a revised flow against the catalog's current copy and calls out every difference in the pull
/// request. Kind-specific on purpose, mirroring <see cref="FlowSetCollector"/>'s per-kind fact extraction, but
/// reduced to the stable identity of each endpoint (no hooks, no options, no schedule), so an edit that only
/// tunes behavior never reads as a repoint.
/// </summary>
public static class FlowDeclaredEndpoints
{
    public static IReadOnlyList<DeclaredEndpoint> Describe(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var endpoints = new List<DeclaredEndpoint>();
        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint(
                    "source", $"{ServerIdentity.From(refs[flow.Source.Server])} {flow.Source.Table.QualifiedName}"));
                endpoints.Add(new DeclaredEndpoint(
                    "target", $"{ServerIdentity.From(refs[flow.Target.Server])} {flow.Target.Table.QualifiedName}"));
                break;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint(
                    "source", $"{ServerIdentity.From(refs[flow.SrcServer])} {flow.Source.QualifiedName}"));
                if (!string.IsNullOrWhiteSpace(flow.TrgPath))
                {
                    endpoints.Add(new DeclaredEndpoint("target", flow.TrgPath!));
                }

                break;
            }

            case TranslateFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint("source", ServerIdentity.From(refs[flow.SrcServer])));
                endpoints.Add(new DeclaredEndpoint("target", flow.Output.Path));
                if (flow.Invoke is { } invoke)
                {
                    endpoints.Add(new DeclaredEndpoint("delivery", invoke.Url));
                }

                break;
            }

            case StoredProcedureFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint(
                    "procedure", $"{ServerIdentity.From(refs[flow.Server])} {flow.Procedure.QualifiedName}"));
                break;
            }

            case HealthCheckFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint(
                    "target", $"{ServerIdentity.From(refs[flow.Server])} {flow.Target.QualifiedName}"));
                break;
            }

            case CalendarFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                endpoints.Add(new DeclaredEndpoint(
                    "target", $"{ServerIdentity.From(refs[flow.Server])} {flow.Table.QualifiedName}"));
                break;
            }

            case FileFlowDocument doc:
            {
                var flow = doc.Flow;
                var location = !string.IsNullOrWhiteSpace(flow.Source.Location)
                    ? flow.Source.Location!
                    : Option(flow.Source.Options, "srcPath");
                if (!string.IsNullOrWhiteSpace(location))
                {
                    endpoints.Add(new DeclaredEndpoint("source", $"{flow.Source.Type} {location}"));
                }

                endpoints.Add(new DeclaredEndpoint(
                    "target",
                    $"{ServerIdentity.From(flow.Target.Connection)} [{flow.Target.Schema}].[{flow.Target.Table}]"));
                break;
            }

            case AcquireFlowDocument doc:
            {
                foreach (var item in doc.Flow.Items)
                {
                    var baseUrl = item.Source.BaseUrl.TrimEnd('/');
                    var path = item.Source.Request?.Path;
                    endpoints.Add(new DeclaredEndpoint(
                        "source",
                        string.IsNullOrWhiteSpace(path)
                            ? baseUrl
                            : baseUrl + (path!.StartsWith('/') ? path : "/" + path)));
                    endpoints.Add(new DeclaredEndpoint(
                        "landing", $"{item.Landing.Target.TrimEnd('/')}/{item.Landing.PathTemplate.TrimStart('/')}"));
                }

                break;
            }

            case CopyFlowDocument doc:
            {
                foreach (var step in doc.Flow.Steps)
                {
                    endpoints.Add(new DeclaredEndpoint("source", step.Source.Location));
                    endpoints.Add(new DeclaredEndpoint("target", step.Target.Location));
                }

                break;
            }

            case SftpFlowDocument doc:
            {
                var flow = doc.Flow;
                var host = $"sftp://{flow.Server.Host}:{flow.Server.Port}";
                foreach (var step in flow.Steps)
                {
                    endpoints.Add(new DeclaredEndpoint("remote", host + step.RemotePath));
                    endpoints.Add(new DeclaredEndpoint("local", step.Local));
                }

                break;
            }

            case InvokeFlowDocument doc:
            {
                foreach (var output in doc.Document.Definition.Outputs)
                {
                    endpoints.Add(new DeclaredEndpoint("output", output.Location));
                }

                break;
            }

            // A source-control snapshot reads definitions, not data, and a batch document declares no flow of
            // its own: neither has a data endpoint whose change the preflight should police.
            case SourceControlFlowDocument:
            case BatchFlowDocument:
                break;

            default:
                throw new SqlFlowException(
                    $"unhandled flow document kind '{document.GetType().Name}'; a new document kind must be added "
                    + "to the declared-endpoint projection.");
        }

        return endpoints;
    }

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<Core.Connections.DataSource> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);

    private static string? Option(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
