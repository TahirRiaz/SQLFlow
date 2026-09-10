using SqlFlow.Azure;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// The "running in Azure" detection that drives the dev-PC trick: off-cloud, managed identity must be skipped so
/// the credential chain does not stall on the IMDS probe that has no endpoint on a developer machine. Tested via
/// the injected environment accessor, deterministically and without touching the process environment.
/// </summary>
public sealed class AzureEnvironmentTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return k => map.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void NoSignals_IsNotAzure()
        => Assert.False(AzureEnvironment.IsRunningInAzure(Env()));

    [Theory]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("FUNCTIONS_WORKER_RUNTIME")]
    [InlineData("MSI_ENDPOINT")]
    [InlineData("IDENTITY_HEADER")]
    [InlineData("AZURE_VM_RESOURCE_GROUP")]
    public void AnyAzureSignal_IsAzure(string signal)
        => Assert.True(AzureEnvironment.IsRunningInAzure(Env((signal, "present"))));

    [Fact]
    public void Override_ForcesAzureOn()
        => Assert.True(AzureEnvironment.IsRunningInAzure(Env(("IS_RUNNING_IN_AZURE", "true"))));

    [Fact]
    public void Override_ForcesAzureOff_EvenWithASignal()
        => Assert.False(AzureEnvironment.IsRunningInAzure(Env(("WEBSITE_INSTANCE_ID", "x"), ("IS_RUNNING_IN_AZURE", "false"))));
}
