using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// How a container authenticates. <see cref="Ambient"/> is the process credential (<see cref="IAzureCredentialFactory"/>:
/// managed identity, service principal or <c>az login</c>, as <c>SQLFLOW_AZURE_AUTH</c> chooses), which every flow uses
/// unless it declares a secret; the others carry a secret a flow's endpoint resolved from a reference (a vendor drop zone
/// that only issues a key or a shared access signature). Each works against both endpoints: a token and a shared access
/// signature authorize Data Lake requests as they do Blob requests, and an account key signs a short-lived account
/// signature for the Data Lake endpoint (see <see cref="DfsSignature"/>).
/// </summary>
public sealed class AzureStorageAuth
{
    private AzureStorageAuth(AzureStorageAuthKind kind, string? secret)
    {
        Kind = kind;
        Secret = secret;
        // The cache key separates clients by their secret without holding the secret itself in a second place.
        CacheKey = secret is null
            ? kind.ToString()
            : $"{kind}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))}";
    }

    /// <summary>The process credential.</summary>
    public static AzureStorageAuth Ambient { get; } = new(AzureStorageAuthKind.Ambient, null);

    /// <summary>A storage connection string (with an account key or a shared access signature).</summary>
    public static AzureStorageAuth FromConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new AzureStorageAuth(AzureStorageAuthKind.ConnectionString, connectionString);
    }

    /// <summary>The storage account's access key, for the account the location names.</summary>
    public static AzureStorageAuth FromAccountKey(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        return new AzureStorageAuth(AzureStorageAuthKind.AccountKey, accountKey);
    }

    /// <summary>A shared access signature (the query string, with or without its leading <c>?</c>).</summary>
    public static AzureStorageAuth FromSharedAccessSignature(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        return new AzureStorageAuth(AzureStorageAuthKind.SharedAccessSignature, signature.Trim().TrimStart('?'));
    }

    internal AzureStorageAuthKind Kind { get; }

    internal string? Secret { get; }

    internal string CacheKey { get; }

    /// <summary>Never the secret: an auth renders as its kind, so it cannot leak into a log line or a message.</summary>
    public override string ToString() => Kind.ToString();
}

/// <summary>The kinds of <see cref="AzureStorageAuth"/>.</summary>
internal enum AzureStorageAuthKind
{
    Ambient,
    ConnectionString,
    AccountKey,
    SharedAccessSignature,
}
