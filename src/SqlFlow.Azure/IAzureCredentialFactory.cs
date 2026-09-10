using Azure.Core;

namespace SqlFlow.Azure;

/// <summary>
/// Produces the Azure <see cref="TokenCredential"/> for the .NET SDK access paths (Key Vault and ADF/Automation
/// invoke). It shares one auth intent with the DuckDB cloud-storage path: both read the same
/// <c>SQLFLOW_AZURE_AUTH</c> mode (via <c>AzureAuth</c>), the storage path turning it into a DuckDB secret rather
/// than a token. One intent, reused everywhere - the basis for "drop it into the cloud and it authenticates itself".
/// </summary>
public interface IAzureCredentialFactory
{
    TokenCredential Create();
}
