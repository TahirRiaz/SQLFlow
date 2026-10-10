using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>Microsoft.Data.SqlClient 7 moved Entra ID authentication out of the core driver: the
/// <c>Authentication=Active Directory *</c> keywords only resolve a provider when
/// Microsoft.Data.SqlClient.Extensions.Azure ships next to the driver, and without it the failure surfaces at
/// connection open, never at build time. Every passwordless method the connection-string canonicalizer accepts
/// (<c>SqlConnectionStringCanonicalizer</c>, rule 2) must resolve a provider here, so dropping the extension
/// package fails this test instead of the first Azure SQL login in production.</summary>
public sealed class SqlEntraAuthenticationProviderTests
{
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity)]
    public void PasswordlessEntraMethod_ResolvesAuthenticationProvider(SqlAuthenticationMethod method)
    {
        Assert.NotNull(SqlAuthenticationProvider.GetProvider(method));
    }
}
