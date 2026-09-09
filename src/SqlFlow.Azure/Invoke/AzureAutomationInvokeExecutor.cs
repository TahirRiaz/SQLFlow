using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using SqlFlow.Core;
using SqlFlow.Core.Invoke;

namespace SqlFlow.Azure.Invoke;

/// <summary>
/// Starts a named Azure Automation runbook job (legacy InvokeType <c>aut</c>) with the flow's parameters and
/// polls the job to a terminal status through the SDK (no raw REST). It runs only a pre-existing, named runbook:
/// no code is shipped from the control database. Job creation is not itself a long-running operation, so the
/// status is polled via <see cref="AutomationJobResource.GetAsync"/>. The legacy engine omitted the runbook
/// parameters; this passes them. A non-<c>Completed</c> terminal status throws (the dispatcher records the
/// failed run, with the job's exception / status detail); the caller's cancellation token bounds the poll.
/// </summary>
public sealed class AzureAutomationInvokeExecutor : IInvokeExecutor
{
    private readonly AzureServicePrincipalResolver _resolver;
    private readonly TimeSpan _pollInterval;

    public AzureAutomationInvokeExecutor(AzureServicePrincipalResolver resolver, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
    }

    public bool CanHandle(InvokeType type) => type == InvokeType.AzureAutomation;

    public async Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var runbookName = NonBlank(definition.RunbookName)
            ?? throw new SqlFlowException($"Invoke '{definition.InvokeAlias}' (aut) has no RunbookName to run.");

        var sp = await _resolver.ResolveAsync(definition.TargetServicePrincipalReference, ct).ConfigureAwait(false);
        var accountName = NonBlank(sp.AutomationAccountName)
            ?? throw new SqlFlowException($"Invoke '{definition.InvokeAlias}' (aut): the service principal has no AutomationAccountName.");

        var arm = new ArmClient(sp.Credential);
        var accountId = new ResourceIdentifier(
            $"/subscriptions/{sp.SubscriptionId}/resourceGroups/{sp.ResourceGroup}/providers/Microsoft.Automation/automationAccounts/{accountName}");
        var account = arm.GetAutomationAccountResource(accountId);

        var content = new AutomationJobCreateOrUpdateContent { RunbookName = runbookName };
        foreach (var parameter in InvokeParameterJson.ToAutomationParameters(definition.ParameterJson))
        {
            content.Parameters.Add(parameter.Key, parameter.Value);
        }

        var jobName = $"{runbookName}_SQLFLW_{Guid.NewGuid()}";
        var operation = await account.GetAutomationJobs()
            .CreateOrUpdateAsync(WaitUntil.Completed, jobName, content, null, ct)
            .ConfigureAwait(false);
        var job = operation.Value;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var latest = await job.GetAsync(null, ct).ConfigureAwait(false);
            var data = latest.Value.Data;
            var status = data.Status;

            if (!IsTerminal(status))
            {
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                continue;
            }

            if (status != AutomationJobStatus.Completed)
            {
                var detail = data.Exception ?? data.StatusDetails;
                throw new SqlFlowException(
                    $"Automation runbook '{runbookName}' job '{jobName}' ended with status '{status}'. {detail}".TrimEnd());
            }

            return new InvokeExecution { StandardOutput = $"jobId={data.JobId}; status={status}" };
        }
    }

    private static bool IsTerminal(AutomationJobStatus? status)
        => status == AutomationJobStatus.Completed
           || status == AutomationJobStatus.Failed
           || status == AutomationJobStatus.Stopped
           || status == AutomationJobStatus.Suspended;

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
