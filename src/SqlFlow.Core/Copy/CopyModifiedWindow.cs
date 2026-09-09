namespace SqlFlow.Core.Copy;

/// <summary>
/// The effective last-modified filter a copy source applies while listing: a file is kept when its last-modified
/// falls within [<see cref="From"/>, <see cref="To"/>]. Either bound may be null (open-ended), and
/// <see cref="Unbounded"/> keeps every file. The copy engine resolves this once per source step, from the run's
/// backfill window (an operational override supplied at trigger time) or, absent that, the endpoint's declared
/// <c>modifiedWithinDays</c> default. Passing the resolved window to every endpoint means the ADLS and local
/// implementations apply one identical rule instead of each re-deriving "now minus N days", and the engine owns
/// the single <see cref="TimeProvider"/> the default is computed against, which keeps selection deterministic
/// under test.
/// </summary>
public readonly record struct CopyModifiedWindow(DateTimeOffset? From, DateTimeOffset? To)
{
    /// <summary>No modified-date filter: every listed file is kept.</summary>
    public static readonly CopyModifiedWindow Unbounded = new(null, null);

    /// <summary>True when this window applies no bound at all.</summary>
    public bool IsUnbounded => From is null && To is null;

    /// <summary>
    /// Whether a file with the given last-modified passes the window. An unbounded window keeps every file. When
    /// a bound is set but the endpoint could not determine the file's modified timestamp, the file is kept: a
    /// modified-date filter cannot exclude a file it cannot date, which preserves the endpoints' prior behavior
    /// (blobs and local files always report a timestamp, so this only guards a theoretically undatable source).
    /// </summary>
    public bool Includes(DateTimeOffset? modified)
    {
        if (IsUnbounded)
        {
            return true;
        }

        if (modified is not { } m)
        {
            return true;
        }

        return (From is not { } from || m >= from) && (To is not { } to || m <= to);
    }
}
