namespace SqlFlow.Core.Connections;

/// <summary>
/// Blob staging context bound to a credential (legacy <c>StorageAccountName</c> / <c>BlobContainer</c>),
/// surfaced so file-source ingestion can stage blobs under the same identity. Empty when not applicable.
/// </summary>
public sealed record StorageContext
{
    public string? StorageAccountName { get; init; }

    public string? BlobContainer { get; init; }

    /// <summary>
    /// New in V3 (no legacy origin): the sovereign-cloud-aware blob endpoint suffix. Null means it is
    /// derived at runtime from the detected cloud; it is not persisted in the control database.
    /// </summary>
    public string? BlobEndpointSuffix { get; init; }
}
