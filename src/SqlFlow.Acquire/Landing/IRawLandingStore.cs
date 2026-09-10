namespace SqlFlow.Acquire.Landing;

/// <summary>
/// The write side of raw landing: persists a fetched payload verbatim to a location, so a downstream SQLFlow file
/// flow can ingest it exactly as if a runbook had uploaded it. Complements the read-only <c>IFileStore</c>. A store
/// advertises the locations it handles (a local/UNC path or an Azure Storage URI) and is selected by location.
/// </summary>
public interface IRawLandingStore
{
    /// <summary>True if this store handles the given base location (a local/UNC path or an Azure Storage URI).</summary>
    bool CanHandle(string location);

    /// <summary>Joins a base location and a relative path into a full location this store can address.</summary>
    string Combine(string baseLocation, string relativePath);

    /// <summary>True if an object already exists at the location (used to honor a no-overwrite policy).</summary>
    Task<bool> ExistsAsync(string location, CancellationToken ct = default);

    /// <summary>Writes the payload, creating any parent directories. Overwrites only when <paramref name="overwrite"/>
    /// is set; when overwriting and <paramref name="skipUnchanged"/> is set, a target that already holds byte-identical
    /// content is left untouched so its last-modified time is not bumped and the downstream file flow is not
    /// re-triggered for a fetch that returned the same file. With <paramref name="skipUnchanged"/> off the payload is
    /// written unconditionally (no comparison). Returns <c>true</c> when bytes were written, <c>false</c> when the
    /// write was skipped as unchanged.</summary>
    Task<bool> PutAsync(string location, ReadOnlyMemory<byte> content, bool overwrite, bool skipUnchanged = true, CancellationToken ct = default);

    /// <summary>
    /// Enumerates the names already landed under <paramref name="baseLocation"/>, relative to it and using '/' as the
    /// separator, recursing into subfolders. Directory placeholders are not returned, only leaf objects. A base that
    /// does not exist yet is an empty list (a flow's first run has landed nothing), never an error: this is the read
    /// side of <c>incremental.source: lake</c> resume, where "nothing landed" must mean "start from the seed" rather
    /// than fail the run.
    /// </summary>
    Task<IReadOnlyList<string>> ListNamesAsync(string baseLocation, CancellationToken ct = default);
}
