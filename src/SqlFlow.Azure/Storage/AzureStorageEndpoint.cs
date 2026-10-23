namespace SqlFlow.Azure.Storage;

/// <summary>
/// The two REST endpoints an Azure Storage account serves its blob data on: the Blob service
/// (<c>&lt;account&gt;.blob.core.windows.net</c>) and the Data Lake Storage service (<c>&lt;account&gt;.dfs.core.windows.net</c>).
/// Both read and write the same files. A network can admit either, both or neither: a private endpoint and its
/// <c>privatelink</c> DNS zone are made per endpoint, so a network wired for only one of them reaches the account only
/// there, and the other name resolves to the public address the account's firewall refuses.
/// </summary>
public enum AzureStorageEndpoint
{
    /// <summary>The Blob service endpoint, <c>&lt;account&gt;.blob.core.windows.net</c>.</summary>
    Blob,

    /// <summary>The Data Lake Storage endpoint, <c>&lt;account&gt;.dfs.core.windows.net</c>.</summary>
    Dfs,
}

/// <summary>One entry of a container listing, the same from either endpoint.</summary>
/// <param name="Name">The in-container path, with no leading or trailing slash.</param>
/// <param name="IsDirectory">True for a directory: an ADLS Gen2 directory, or a virtual folder of a flat namespace.</param>
/// <param name="Length">The file's length in bytes; 0 for a directory.</param>
/// <param name="LastModified">When the file last changed, when the listing reports it.</param>
/// <param name="ContentHash">The MD5 the file stores as its content hash, when the listing reports one.</param>
/// <param name="ContentHashListed">True when the listing reports content hashes at all (the Blob endpoint does), so a
/// <c>null</c> <paramref name="ContentHash"/> means the file stores none. False from the Data Lake endpoint, whose
/// listing carries no hashes: <see cref="AzureStorageContainer.GetPropertiesAsync"/> reads the stored hash.</param>
public sealed record AzureStorageItem(
    string Name, bool IsDirectory, long Length, DateTimeOffset? LastModified, byte[]? ContentHash, bool ContentHashListed);

/// <summary>The properties of one path, the same from either endpoint.</summary>
/// <param name="Length">The file's length in bytes; 0 for a directory.</param>
/// <param name="LastModified">When the path last changed.</param>
/// <param name="IsDirectory">True for a directory (an ADLS Gen2 directory or its directory-marker blob).</param>
/// <param name="ContentHash">The MD5 the file stores as its content hash, or <c>null</c> when it stores none.</param>
/// <param name="ETag">The path's current entity tag.</param>
public sealed record AzureStorageProperties(
    long Length, DateTimeOffset? LastModified, bool IsDirectory, byte[]? ContentHash, string? ETag);
