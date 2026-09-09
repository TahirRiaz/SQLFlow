namespace SqlFlow.Core.Invoke;

/// <summary>
/// The kind of external action an flw.Invoke flow triggers. Invoke only ever triggers a named, pre-existing
/// external resource with typed parameters: <c>adf</c> = an Azure Data Factory pipeline, <c>aut</c> = an Azure
/// Automation runbook. The legacy host-side code/script execution types (PowerShell <c>ps</c> and dynamic C#
/// <c>cs</c>) were deliberately removed: storing executable text in the control database and running it on the
/// engine host is a code-injection / remote-execution risk, and dynamic C# is unmaintainable because the script
/// host cannot reference the arbitrary libraries user code reaches for. File and document reshaping that the
/// legacy <c>cs</c> path performed is handled by the XML/JSON flattener and the typed file pipeline instead.
/// </summary>
public enum InvokeType
{
    /// <summary>An Azure Data Factory pipeline run (legacy code <c>adf</c>).</summary>
    AzureDataFactory,

    /// <summary>An Azure Automation runbook job (legacy code <c>aut</c>).</summary>
    AzureAutomation,
}

/// <summary>Translates between the V3 <see cref="InvokeType"/> and the legacy short codes that rest in
/// flw.Invoke.InvokeType and surface as the SysLog FlowType.</summary>
public static class InvokeTypeCodes
{
    /// <summary>Parses a legacy code into an <see cref="InvokeType"/>. A blank value maps to
    /// <see cref="InvokeType.AzureAutomation"/> (the flw.Invoke column default). The removed host-execution codes
    /// <c>ps</c> and <c>cs</c> fail fast with a precise reason, so a legacy row carrying them is flagged for
    /// manual migration rather than silently accepted; any other code is rejected generically.</summary>
    public static InvokeType Parse(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return InvokeType.AzureAutomation;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "adf" => InvokeType.AzureDataFactory,
            "aut" => InvokeType.AzureAutomation,
            "ps" or "cs" => throw new SqlFlowException(
                $"InvokeType '{code}' (host script/code execution) has been removed as a code-injection risk. " +
                "Invoke now only triggers Azure Data Factory pipelines ('adf') and Azure Automation runbooks ('aut')."),
            _ => throw new SqlFlowException($"Unknown InvokeType '{code}'. Expected one of: adf, aut."),
        };
    }

    /// <summary>The legacy short code for an <see cref="InvokeType"/> (used as the SysLog FlowType).</summary>
    public static string ToCode(InvokeType type) => type switch
    {
        InvokeType.AzureDataFactory => "adf",
        InvokeType.AzureAutomation => "aut",
        _ => throw new SqlFlowException($"Unhandled InvokeType '{type}'."),
    };
}
