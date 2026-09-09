using Azure.ResourceManager;
using Azure.ResourceManager.DataFactory;
using SqlFlow.Core;
using SqlFlow.Core.Invoke;

namespace SqlFlow.Azure.Invoke;

/// <summary>
/// Triggers a named Azure Data Factory pipeline (legacy InvokeType <c>adf</c>) with the flow's JSON parameters
/// and polls the run to a terminal status. It executes only a pre-existing, named pipeline: no code is shipped
/// from the control database. Authentication and the subscription / resource-group / factory coordinates come
/// from the secretless service-principal alias on the flow. A non-<c>Succeeded</c> terminal status throws (the
/// dispatcher records the failed run); the caller's cancellation token bounds the poll.
/// </summary>
public sealed class AzureDataFactoryInvokeExecutor : IInvokeExecutor
{
    private static readonly HashSet<string> NonTerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Queued", "InProgress", "Canceling" };

    private readonly AzureServicePrincipalResolver _resolver;
    private readonly TimeSpan _pollInterval;

    public AzureDataFactoryInvokeExecutor(AzureServicePrincipalResolver resolver, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(15);
    }

    public bool CanHandle(InvokeType type) => type == InvokeType.AzureDataFactory;

    public async Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var pipelineName = NonBlank(definition.PipelineName)
            ?? throw new SqlFlowException($"Invoke '{definition.InvokeAlias}' (adf) has no PipelineName to run.");

        var sp = await _resolver.ResolveAsync(definition.TargetServicePrincipalReference, ct).ConfigureAwait(false);
        var factoryName = NonBlank(sp.DataFactoryName)
            ?? throw new SqlFlowException($"Invoke '{definition.InvokeAlias}' (adf): the service principal has no DataFactoryName.");

        var arm = new ArmClient(sp.Credential);
        var factoryId = DataFactoryResource.CreateResourceIdentifier(sp.SubscriptionId, sp.ResourceGroup, factoryName);
        var dataFactory = arm.GetDataFactoryResource(factoryId);

        var pipeline = (await dataFactory.GetDataFactoryPipelineAsync(pipelineName, cancellationToken: ct).ConfigureAwait(false)).Value;
        var parameters = InvokeParameterJson.ToDataFactoryParameters(definition.ParameterJson);
        var run = (await pipeline.CreateRunAsync(parameters, cancellationToken: ct).ConfigureAwait(false)).Value;
        var runId = run.RunId.ToString();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var info = (await dataFactory.GetPipelineRunAsync(runId, ct).ConfigureAwait(false)).Value;
            var status = info.Status;

            // A not-yet-populated or non-terminal status keeps polling; an unrecognised value is treated as
            // still-running so a new service state never causes a premature (false) success.
            if (string.IsNullOrEmpty(status) || NonTerminalStatuses.Contains(status))
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                continue;
            }

            if (!string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlFlowException(
                    $"ADF pipeline '{pipelineName}' run {runId} ended with status '{status}'. {info.Message}".TrimEnd());
            }

            return new InvokeExecution { StandardOutput = $"runId={runId}; status={status}" };
        }
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
