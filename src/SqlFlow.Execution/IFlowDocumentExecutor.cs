using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>
/// Runs the documents of a flow kind a host registered (<see cref="IFlowDocumentKind"/>). The
/// <see cref="DocumentExecutor"/> hands a <see cref="RegisteredFlowDocument"/> to the first registered executor that
/// claims it, so a module's kind runs through the same entry point as every built-in kind: the CLI's <c>run</c>, a
/// batch member and a node's claimed run.
/// </summary>
public interface IFlowDocumentExecutor
{
    /// <summary>Whether this executor runs <paramref name="document"/>.</summary>
    bool CanExecute(RegisteredFlowDocument document);

    /// <summary>Runs the document and writes its run-history artifacts, returning the rich result.</summary>
    Task<DocumentExecutionResult> ExecuteAsync(
        RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct);
}
