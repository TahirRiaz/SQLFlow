using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

namespace SqlFlow.Lineage.Collection;

/// <summary>
/// One runnable flow a document declares, as every catalog consumer must see it: the name, kind, batch, server
/// identities, execution mode, lifecycle, and schedule a pipeline row derives from the document itself. The first
/// header is always the document's primary flow and carries the document's <c>schedule:</c> block; a derived sibling
/// (an ingestion document's embedded <c>healthCheck:</c>) never carries it, following its own mode instead.
/// </summary>
/// <param name="Name">The flow's name: its pipeline identity and its run-history folder.</param>
/// <param name="Kind">The flow kind (file / ing / api / cpy / sftp / exp / trl / sp / inv / hc / cal / scm).</param>
/// <param name="Batch">The flow's grouping label, or null when it declares none.</param>
/// <param name="SourceServerRef">The source-side server identity, when the kind reads one.</param>
/// <param name="TargetServerRef">The server identity statements run against by default (the target side).</param>
/// <param name="Schedule">The document's <c>schedule:</c> declaration, carried by the primary header only.</param>
/// <param name="Mode">The flow's execution mode.</param>
/// <param name="Lifecycle">The flow's declared lifecycle (production unless the document says otherwise).</param>
/// <param name="ParticipatesInLineage">
/// Whether this flow belongs in the lineage graph. True for every flow that moves catalog data. False for a
/// maintenance flow that runs on the estate rather than through it (<c>scm</c>): it reads a database's
/// definitions and writes a git tree, so it produces no data dependency, must never join a wave, and must never
/// pull a batch into a false ordering. Such a flow is still a full pipeline row with its own schedule and run
/// history; only the graph excludes it.
/// </param>
public sealed record DocumentFlowHeader(
    string Name,
    string Kind,
    string? Batch,
    string? SourceServerRef,
    string TargetServerRef,
    ScheduleSpec? Schedule,
    ExecutionMode Mode = ExecutionMode.Auto,
    FlowLifecycle Lifecycle = FlowLifecycle.Production,
    bool ParticipatesInLineage = true);

/// <summary>
/// THE single authority for "what flows does this document contain": the estate scan builds its lineage flow
/// nodes from this projection and the per-run catalog write-back builds its pipeline rows and schedule mirror
/// from it, so every element of a parsed document is extracted identically no matter which path triggered the
/// parse. A batch document declares no runnable flow of its own and projects to an empty list; a source-control
/// document projects a real flow marked <see cref="DocumentFlowHeader.ParticipatesInLineage"/> false, so it is a
/// schedulable pipeline that stays out of the graph. A document kind this projection does not know is a
/// programming error and throws, so a future kind can never be silently dropped by one consumer while the other
/// handles it.
/// </summary>
public static class FlowDocumentHeaders
{
    public static IReadOnlyList<DocumentFlowHeader> Project(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        switch (document)
        {
            case IngestionFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var target = ServerIdentity.From(refs[flow.Target.Server]);
                var headers = new List<DocumentFlowHeader>
                {
                    new(flow.SysAlias ?? flow.Target.Table.Name, "ing", flow.Batch,
                        ServerIdentity.From(refs[flow.Source.Server]), target, document.Schedule,
                        document.Mode, flow.Lifecycle),
                };

                // The embedded healthCheck: block is a full sibling pipeline sharing this file. The document's
                // schedule: block deliberately stays on the load; the check follows its own mode and the load's
                // lifecycle.
                if (doc.Document.HealthCheck is { } check)
                {
                    headers.Add(new DocumentFlowHeader(
                        check.SysAlias, "hc", check.Batch, null, target, Schedule: null, check.Mode, flow.Lifecycle));
                }

                return headers;
            }

            case ExportFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var server = ServerIdentity.From(refs[flow.SrcServer]);
                return [new DocumentFlowHeader(flow.SysAlias, "exp", flow.Batch, server, server, document.Schedule, document.Mode, flow.Lifecycle)];
            }

            case TranslateFlowDocument doc:
            {
                // A translation reads its declared query on the source server and writes files (and optionally
                // an API endpoint); like an export, its one relational server is both sides of the header.
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                var server = ServerIdentity.From(refs[flow.SrcServer]);
                return [new DocumentFlowHeader(flow.SysAlias, "trl", flow.Batch, server, server, document.Schedule, document.Mode, flow.Lifecycle)];
            }

            case StoredProcedureFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(flow.SysAlias, "sp", flow.Batch, null, ServerIdentity.From(refs[flow.Server]), document.Schedule, document.Mode, flow.Lifecycle),
                ];
            }

            case HealthCheckFlowDocument doc:
            {
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(flow.SysAlias, "hc", flow.Batch, null, ServerIdentity.From(refs[flow.Server]), document.Schedule, flow.Mode, flow.Lifecycle),
                ];
            }

            case CalendarFlowDocument doc:
            {
                // A calendar flow has no source: it generates its rows. Its declared server is therefore both
                // sides of the header, exactly as an export flow's is.
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(flow.SysAlias, "cal", flow.Batch, null, ServerIdentity.From(refs[flow.Server]), document.Schedule, document.Mode, flow.Lifecycle),
                ];
            }

            case FileFlowDocument doc:
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "file", doc.Flow.Batch, null, ServerIdentity.From(doc.Flow.Target.Connection), document.Schedule, document.Mode, doc.Flow.Lifecycle),
                ];

            case InvokeFlowDocument doc:
                // An invoke triggers external compute; it moves no catalog data itself.
                return
                [
                    new DocumentFlowHeader(doc.Document.Definition.InvokeAlias, "inv", doc.Document.Definition.Batch, null, ServerIdentity.FileSystem, document.Schedule, document.Mode, doc.Document.Definition.Lifecycle),
                ];

            case AcquireFlowDocument doc:
                // An acquisition flow fetches from a third party and writes raw files to the lake; its landing
                // target is its declared output.
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "api", doc.Flow.Batch, null, ServerIdentity.FileSystem, document.Schedule, document.Mode),
                ];

            case CopyFlowDocument doc:
                // A copy flow moves files between endpoints (both sides are file locations); it reads its source
                // and writes its target, so it is a filesystem flow.
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "cpy", doc.Flow.Batch, null, ServerIdentity.FileSystem, document.Schedule, document.Mode),
                ];

            case SftpFlowDocument doc:
                // An SFTP flow transfers files between a server and the lake/local; both sides are file locations.
                return
                [
                    new DocumentFlowHeader(doc.Flow.Name, "sftp", doc.Flow.Batch, null, ServerIdentity.FileSystem, document.Schedule, document.Mode),
                ];

            case SourceControlFlowDocument doc:
            {
                // A source-control snapshot is a real, schedulable flow: it reads one SQL Server database's
                // object definitions and writes them into a git working tree, so its source is that server and
                // its target is the file system. It carries no data between catalog objects, so it is marked out
                // of the lineage graph (no node, no edges, no wave) while remaining a full pipeline row with its
                // own schedule and run history.
                var flow = doc.Document.Flow;
                var refs = ConnectionRefs(doc.Document.Connections);
                return
                [
                    new DocumentFlowHeader(
                        flow.SysAlias, "scm", flow.Batch, ServerIdentity.From(refs[flow.Server]),
                        ServerIdentity.FileSystem, document.Schedule,
                        document.Mode, flow.Lifecycle, ParticipatesInLineage: false),
                ];
            }

            case BatchFlowDocument:
                // An orchestration document: a batch declares no flow of its own; its ordering is computed FROM
                // lineage, so it never becomes a pipeline row.
                return [];

            default:
                throw new SqlFlowException(
                    $"unhandled flow document kind '{document.GetType().Name}'; a new document kind must be added to the shared header projection.");
        }
    }

    private static Dictionary<string, string> ConnectionRefs(IEnumerable<Core.Connections.DataSource> connections)
        => connections.ToDictionary(c => c.Alias, c => c.ConnectionRef, StringComparer.OrdinalIgnoreCase);
}
