namespace SqlFlow.Yaml;

// Binding-only DTOs for the invoke surface: the standalone invoke document (flowType: inv) and the
// 'invokes:' / 'servicePrincipals:' blocks shared by the ing/exp/sp documents. Every property is nullable;
// all defaults and validation live in YamlInvokeParts / YamlInvokeFlowLoader.

internal sealed class InvokeDocumentYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }
    public Dictionary<string, ServicePrincipalYaml>? ServicePrincipals { get; set; }
    public InvokeBlockYaml? Invoke { get; set; }
    public bool? OnErrorResume { get; set; }
}

/// <summary>One named invoke: an Azure Data Factory pipeline or Automation runbook trigger. The value of an
/// entry under <c>invokes:</c>, and the <c>invoke:</c> section of a standalone document.</summary>
internal sealed class InvokeBlockYaml
{
    public string? Type { get; set; }
    public string? Pipeline { get; set; }
    public string? Runbook { get; set; }
    public string? ServicePrincipal { get; set; }

    /// <summary>Parameter name to value. Plain YAML scalars keep their types into the parameter JSON
    /// (true stays a boolean, 42 a number); quote a scalar to force a string.</summary>
    public Dictionary<string, object?>? Parameters { get; set; }

    /// <summary>A single file drop the triggered pipeline/runbook lands, so lineage links this invoke to the
    /// downstream file ingestion that reads it. Convenience for the common one-folder case; combine with, or use
    /// instead of, <see cref="Outputs"/>.</summary>
    public FileOutputYaml? Output { get; set; }

    /// <summary>Several file drops the triggered compute lands (an SFTP download of many files, or a pipeline that
    /// writes several file sets into several folders): one entry per distinct folder/pattern. Each binds
    /// independently to the ingestion that reads it.</summary>
    public List<FileOutputYaml>? Outputs { get; set; }

    public bool? OnErrorResume { get; set; }
}

internal sealed class ServicePrincipalYaml
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }

    /// <summary>A whole <c>${env:...}</c> / <c>${keyvault:...}</c> reference ONLY; the secret value itself
    /// never rests in the document. Omit it to authenticate with the ambient Azure credential.</summary>
    public string? ClientSecret { get; set; }

    public string? SubscriptionId { get; set; }
    public string? ResourceGroup { get; set; }
    public string? DataFactoryName { get; set; }
    public string? AutomationAccountName { get; set; }
    public string? KeyVaultName { get; set; }
}
