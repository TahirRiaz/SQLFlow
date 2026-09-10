using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core.Secrets;
using SqlFlow.SqlServer.FullMode;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Live triggering of the Azure invoke executors against a real Data Factory pipeline and Automation runbook.
/// These run only when the Azure test environment variables are set (see <see cref="AzureTestEnv"/>); otherwise
/// they skip, exactly like the SQL integration tests skip without SQLFlowSinkConStr. The secret never rests in
/// the control DB: flw.ServicePrincipal stores a ${env:...} reference to SQLFLOW_AZTEST_CLIENT_SECRET, expanded
/// at runtime by the environment secret provider.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureInvokeLiveIntegrationTests
{
    private const string SecretRef = "${env:SQLFLOW_AZTEST_CLIENT_SECRET}";

    [SkippableFact]
    public async Task FullMode_AdfInvoke_TriggersPipeline_ToSuccess()
    {
        var cs = IntegrationDb.Require();
        var az = AzureTestEnv.RequireDataFactory();
        await ControlPlaneSchema.EnsureAsync(cs);

        const int flowId = 90;
        const string spAlias = "sf_az_adf_sp";
        await Clean(cs, flowId, spAlias);

        try
        {
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[ServicePrincipal] " +
                "([ServicePrincipalAlias],[TenantId],[ClientId],[ClientSecretRef],[SubscriptionId],[ResourceGroup],[DataFactoryName]) " +
                $"VALUES ('{spAlias}','{Esc(az.Tenant)}','{Esc(az.ClientId)}','{SecretRef}','{Esc(az.Subscription)}','{Esc(az.ResourceGroup)}','{Esc(az.DataFactory)}');");
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[Invoke] ([FlowID],[SysAlias],[InvokeAlias],[InvokeType],[PipelineName],[trgServicePrincipalAlias]) " +
                $"VALUES ({flowId},'inv','sf-az-adf','adf','{Esc(az.Pipeline)}','{spAlias}');");

            var host = BuildHost(cs);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await host.RunInvokeByIdAsync(flowId, cts.Token);

            Assert.True(result.Success, result.Error);
            Assert.Equal("adf", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [FlowType] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int,[Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await Clean(cs, flowId, spAlias);
        }
    }

    [SkippableFact]
    public async Task FullMode_AutomationInvoke_TriggersRunbook_ToSuccess()
    {
        var cs = IntegrationDb.Require();
        var az = AzureTestEnv.RequireAutomation();
        await ControlPlaneSchema.EnsureAsync(cs);

        const int flowId = 9103;
        const string spAlias = "sf_az_aut_sp";
        await Clean(cs, flowId, spAlias);

        try
        {
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[ServicePrincipal] " +
                "([ServicePrincipalAlias],[TenantId],[ClientId],[ClientSecretRef],[SubscriptionId],[ResourceGroup],[AutomationAccountName]) " +
                $"VALUES ('{spAlias}','{Esc(az.Tenant)}','{Esc(az.ClientId)}','{SecretRef}','{Esc(az.Subscription)}','{Esc(az.ResourceGroup)}','{Esc(az.AutomationAccount)}');");
            await IntegrationDb.ExecuteAsync(cs,
                "INSERT INTO [flw].[Invoke] ([FlowID],[SysAlias],[InvokeAlias],[InvokeType],[RunbookName],[trgServicePrincipalAlias]) " +
                $"VALUES ({flowId},'inv','sf-az-aut','aut','{Esc(az.Runbook)}','{spAlias}');");

            var host = BuildHost(cs);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var result = await host.RunInvokeByIdAsync(flowId, cts.Token);

            Assert.True(result.Success, result.Error);
            Assert.Equal("aut", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [FlowType] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int,[Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await Clean(cs, flowId, spAlias);
        }
    }

    private static FullModeIngestionHost BuildHost(string cs)
    {
        var secrets = new SecretResolver([new EnvSecretProvider()]);
        var executors = AzureInvokeExecutors.Create(new SqlServicePrincipalStore(cs), secrets, new AzureCredentialFactory());
        return FullModeIngestionHost.Create(new FullModeOptions
        {
            ControlConnectionString = cs,
            Secrets = secrets,
            InvokeExecutors = executors,
        });
    }

    private static string Esc(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static Task Clean(string cs, int flowId, string spAlias)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.Invoke','U') IS NOT NULL DELETE FROM [flw].[Invoke] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.ServicePrincipal','U') IS NOT NULL DELETE FROM [flw].[ServicePrincipal] WHERE [ServicePrincipalAlias] = '{spAlias}'; " +
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");
}

/// <summary>
/// Gates the live Azure invoke tests on a configured service principal + target resource. Set these to run them;
/// leave them unset and the tests skip. The client secret lives ONLY in the environment (the control DB stores a
/// ${env:SQLFLOW_AZTEST_CLIENT_SECRET} reference), so the secret never rests in SQL.
/// </summary>
internal static class AzureTestEnv
{
    public sealed record DataFactoryConfig(string Tenant, string ClientId, string Subscription, string ResourceGroup, string DataFactory, string Pipeline);

    public sealed record AutomationConfig(string Tenant, string ClientId, string Subscription, string ResourceGroup, string AutomationAccount, string Runbook);

    public static DataFactoryConfig RequireDataFactory()
    {
        var (tenant, clientId, subscription, resourceGroup, secretSet) = Core();
        var dataFactory = Env("SQLFLOW_AZTEST_DATA_FACTORY_NAME");
        var pipeline = Env("SQLFLOW_AZTEST_PIPELINE_NAME");
        Skip.If(
            !secretSet || AnyBlank(tenant, clientId, subscription, resourceGroup, dataFactory, pipeline),
            "Set SQLFLOW_AZTEST_TENANT_ID / _CLIENT_ID / _CLIENT_SECRET / _SUBSCRIPTION_ID / _RESOURCE_GROUP / _DATA_FACTORY_NAME / _PIPELINE_NAME to run the live ADF invoke test.");
        return new DataFactoryConfig(tenant!, clientId!, subscription!, resourceGroup!, dataFactory!, pipeline!);
    }

    public static string RequireKeyVault()
    {
        var vault = Env("SQLFLOW_AZTEST_KEYVAULT_NAME");
        Skip.If(
            string.IsNullOrWhiteSpace(vault),
            "Set SQLFLOW_AZTEST_KEYVAULT_NAME (plus ambient Azure auth: SQLFLOW_AZURE_AUTH / az login / managed identity) to run the live Key Vault read/write tests.");
        return vault!;
    }

    public static AutomationConfig RequireAutomation()
    {
        var (tenant, clientId, subscription, resourceGroup, secretSet) = Core();
        var account = Env("SQLFLOW_AZTEST_AUTOMATION_ACCOUNT");
        var runbook = Env("SQLFLOW_AZTEST_RUNBOOK_NAME");
        Skip.If(
            !secretSet || AnyBlank(tenant, clientId, subscription, resourceGroup, account, runbook),
            "Set SQLFLOW_AZTEST_TENANT_ID / _CLIENT_ID / _CLIENT_SECRET / _SUBSCRIPTION_ID / _RESOURCE_GROUP / _AUTOMATION_ACCOUNT / _RUNBOOK_NAME to run the live Automation invoke test.");
        return new AutomationConfig(tenant!, clientId!, subscription!, resourceGroup!, account!, runbook!);
    }

    private static (string? Tenant, string? ClientId, string? Subscription, string? ResourceGroup, bool SecretSet) Core()
        => (Env("SQLFLOW_AZTEST_TENANT_ID"), Env("SQLFLOW_AZTEST_CLIENT_ID"), Env("SQLFLOW_AZTEST_SUBSCRIPTION_ID"),
            Env("SQLFLOW_AZTEST_RESOURCE_GROUP"), !string.IsNullOrWhiteSpace(Env("SQLFLOW_AZTEST_CLIENT_SECRET")));

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static bool AnyBlank(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);
}
