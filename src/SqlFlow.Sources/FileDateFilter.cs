using System.Text.RegularExpressions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// What a walk examined and why it rejected what it rejected. An empty selection has several distinct causes -
/// the location holds no candidate at all, or it holds plenty and every one sits at or before the watermark -
/// and only the walk can tell them apart: the filter runs inside the store's enumeration, so by the time the
/// caller sees an empty list the evidence is gone. Tallying during the walk classifies the outcome without a
/// second, unpruned listing of the whole tree.
/// </summary>
/// <param name="Tested">Files that matched the glob and were put to the filter.</param>
/// <param name="MaskRejected">Of those, how many the path mask rejected.</param>
/// <param name="DateRejected">Of those, how many passed the mask but fell outside the date window.</param>
/// <param name="PrunedDirectories">Folders skipped whole as provably out of window.</param>
/// <param name="Undated">Of the date-rejected files, how many carried no parsable date in their name or path.</param>
/// <param name="EarliestRejected">The earliest date (UTC) a date-rejected file carried; null when none carried one.</param>
/// <param name="LatestRejected">The latest date (UTC) a date-rejected file carried; null when none carried one.</param>
/// <param name="Deferred">Of those, how many passed every bound but were modified in or after the filter's settling
/// second, and so are not read by this listing (see <see cref="FileDateFilter"/>).</param>
internal readonly record struct FileDateTally(
    int Tested,
    int MaskRejected,
    int DateRejected,
    int PrunedDirectories,
    int Undated = 0,
    DateTime? EarliestRejected = null,
    DateTime? LatestRejected = null,
    int Deferred = 0);

/// <summary>
/// The discovery-time file selector: the optional path mask and the date window, applied during listing so an
/// out-of-window partition folder is pruned before it is walked and out-of-window files never enter the result.
/// The date is read where the fileDate spec says (path or name); a null spec keeps the legacy behavior of
/// reading the file's modified timestamp. The window bounds come from the flow's init date window and the injected
/// incremental watermark, so init-load and incremental share this one selection path.
/// <para>
/// A file dated by its modified time is compared at the precision <c>FileDate_DW</c> stores that time: the whole
/// second. The watermark is read back from that column, so comparing the file's full-precision time against it
/// would find the file that set the watermark newer than itself (05:51:27.139 is after 05:51:27) and reload it on
/// every run, while the next flow, which compares the stored seconds, finds nothing new in it. Comparing seconds
/// makes a file the watermark already covers read as covered.
/// </para>
/// <para>
/// Whole seconds have one consequence the selection must handle: a file loaded during the second it was written
/// fixes the watermark at that second, and a second file written later in the same second would then never be
/// newer than it. With <c>settledBefore</c> set (the engine sets it on every run of a file-date incremental flow), a
/// file whose modified second is not before it is not read by this listing. The reader starts with the second its
/// listing began in; when that holds a file back, it waits for the second to end and lists again with the next
/// second as the bound, so every file of the first second is read and only a file modified after the run began
/// listing waits for the next run. A file with a modified time in the future waits the same way, so a skewed clock
/// cannot push the watermark past files that have yet to arrive.
/// </para>
/// </summary>
internal sealed class FileDateFilter : IFileDiscoveryFilter
{
    private readonly FileDateSpec? _spec;
    private readonly DateTime? _from;
    private readonly DateTime? _to;
    private readonly DateTime? _after;
    private readonly Regex? _pathMask;
    private readonly DateTime? _settledBefore;

    // What the walk saw, for <see cref="Tally"/>. A store may enumerate sibling folders concurrently
    // (AzureBlobFileStore fans out its descent), so every mutation is interlocked.
    private int _tested;
    private int _maskRejected;
    private int _dateRejected;
    private int _prunedDirectories;
    private int _undated;
    private int _deferred;

    // The span of dates the date-rejected files carried, so an empty selection can say what the files are dated
    // and not only that the window excluded them. Two values move together, so they are guarded by a lock rather
    // than interlocked; it is taken only for a rejected file, never for one the run reads.
    private readonly Lock _rejectedDatesLock = new();
    private DateTime? _earliestRejected;
    private DateTime? _latestRejected;

    /// <param name="spec">Where a file's date is read; null reads its modified time.</param>
    /// <param name="from">The inclusive lower bound of the init date window, or null.</param>
    /// <param name="to">The inclusive upper bound of the init date window, or null.</param>
    /// <param name="after">The incremental watermark a file must be newer than, or null.</param>
    /// <param name="pathMask">The regex a file's path must match, or null.</param>
    /// <param name="settledBefore">The whole second (UTC) the store is listed in: a file dated by its modified time
    /// whose second is not before it waits for the next run. Null reads every file whatever its age.</param>
    public FileDateFilter(FileDateSpec? spec, DateTime? from, DateTime? to, DateTime? after, Regex? pathMask, DateTime? settledBefore = null)
    {
        _spec = spec;
        _from = from;
        _to = to;
        _after = after;
        _pathMask = pathMask;
        _settledBefore = settledBefore;
    }

    /// <summary>True when at least one of the filters is active; a filter with nothing to enforce is not built.</summary>
    public bool IsActive => _spec is not null || _from is not null || _to is not null || _after is not null || _pathMask is not null
        || IsSettling;

    // Only a file dated by its modified time can be unsettled: a name or path carries a date the writer chose, not
    // the moment the write happened.
    private bool IsSettling => _settledBefore is not null && _spec is null;

    /// <summary>The whole second <paramref name="utc"/> falls in, the precision <c>FileDate_DW</c> stores a file date at.</summary>
    internal static DateTime WholeSecond(DateTime utc)
        => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    /// <summary>
    /// What this filter saw and rejected. Meaningful once the listing has completed; a filter that was never
    /// handed to a store (see <see cref="IsActive"/>) reports all zeros, which reads correctly as "nothing was
    /// filtered out", leaving the glob as the only possible explanation for an empty result.
    /// </summary>
    public FileDateTally Tally
    {
        get
        {
            DateTime? earliest;
            DateTime? latest;
            lock (_rejectedDatesLock)
            {
                earliest = _earliestRejected;
                latest = _latestRejected;
            }

            return new(
                Volatile.Read(ref _tested),
                Volatile.Read(ref _maskRejected),
                Volatile.Read(ref _dateRejected),
                Volatile.Read(ref _prunedDirectories),
                Volatile.Read(ref _undated),
                earliest,
                latest,
                Volatile.Read(ref _deferred));
        }
    }

    public bool ShouldEnterDirectory(string directoryPath)
    {
        // Only a path-derived date can rule a whole folder out; a name/modified date, or a folder that carries no
        // date tokens yet, is not prunable, so descend and let the per-file test decide.
        if (_spec is null || _spec.Source != FileDateSource.Path)
        {
            return true;
        }

        if (!HasWindow)
        {
            return true;
        }

        var interval = _spec.Extract(directoryPath);
        if (interval is null || Overlaps(interval.Value))
        {
            return true;
        }

        Interlocked.Increment(ref _prunedDirectories);
        return false;
    }

    public bool Includes(FileRef file)
    {
        Interlocked.Increment(ref _tested);

        if (_pathMask is not null && !_pathMask.IsMatch(file.Path))
        {
            Interlocked.Increment(ref _maskRejected);
            return false;
        }

        if (HasWindow && !InWindow(file))
        {
            return false;
        }

        // Checked last, so a file is deferred only when it would otherwise be read, and the tally's deferred count
        // means exactly "these wait for the next run".
        if (IsSettling && file.Modified is { } written && WholeSecond(written.UtcDateTime) >= _settledBefore!.Value)
        {
            Interlocked.Increment(ref _deferred);
            return false;
        }

        return true;
    }

    private bool InWindow(FileRef file)
    {
        bool inWindow;
        DateTime? fileDate;
        if (_spec is null)
        {
            // The second FileDate_DW stamps, not the full-precision time (see the class remarks).
            var modified = WholeSecond((file.Modified ?? DateTimeOffset.UtcNow).UtcDateTime);
            inWindow = Overlaps(new DateInterval(modified, modified));
            fileDate = modified;
        }
        else
        {
            var text = _spec.Source == FileDateSource.Path ? file.Path : file.Name;
            var interval = _spec.Extract(text);

            // A file that carries no parsable date is not part of a date-scoped selection: exclude it rather than
            // guess. With no window set (HasWindow false) the caller never asks, so an undated file still loads then.
            inWindow = interval is not null && Overlaps(interval.Value);

            // The start of the interval is the file's business timestamp, the same instant FileDate_DW is stamped with.
            fileDate = interval?.Lo;
        }

        if (!inWindow)
        {
            Interlocked.Increment(ref _dateRejected);
            NoteRejected(fileDate);
        }

        return inWindow;
    }

    private void NoteRejected(DateTime? fileDate)
    {
        if (fileDate is not { } date)
        {
            Interlocked.Increment(ref _undated);
            return;
        }

        lock (_rejectedDatesLock)
        {
            if (_earliestRejected is null || date < _earliestRejected)
            {
                _earliestRejected = date;
            }

            if (_latestRejected is null || date > _latestRejected)
            {
                _latestRejected = date;
            }
        }
    }

    private bool HasWindow => _from is not null || _to is not null || _after is not null;

    private bool Overlaps(DateInterval interval)
        => (_from is null || interval.Hi >= _from)
            && (_to is null || interval.Lo <= _to)
            && (_after is null || interval.Hi > _after);
}
