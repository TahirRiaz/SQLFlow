namespace SqlFlow.Core.Secrets;

/// <summary>Resolves <c>${env:NAME}</c> references from environment variables. Always available.</summary>
public sealed class EnvSecretProvider : ISecretProvider
{
    public string Scheme => "env";

    public string Resolve(string locator)
        => Environment.GetEnvironmentVariable(locator)
           ?? throw new SqlFlowException($"Environment variable '{locator}' is not set.");
}
