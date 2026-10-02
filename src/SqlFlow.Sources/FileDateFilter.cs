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
internal readonly record struct FileDateTally(
    int Tested,
    int MaskRejected,
    int DateRejected,
    int PrunedDirectories,
    int Undated = 0,
    DateTime? EarliestRejected = null,
    DateTime? LatestRejected = null);

/// <summary>
/// The discovery-time file selector: the optional path mask and the date window, applied during listing so an
/// out-of-window partition folder is pruned before it is walked and out-of-window files never enter the result.
/// The date is read where the fileDate spec says (path or name); a null spec keeps the legacy behavior of
/// reading the file's modified timestamp. The window bounds come from the flow's init date window and the injected
/// incremental watermark, so init-load and incremental share this one selection path.
/// </summary>
internal sealed class FileDateFilter : IFileDiscoveryFilter
{
    private readonly FileDateSpec? _spec;
    private readonly DateTime? _from;
    private readonly DateTime? _to;
    private readonly DateTime? _after;
    private readonly Regex? _pathMask;

    // What the walk saw, for <see cref="Tally"/>. A store may enumerate sibling folders concurrently
    // (AzureBlobFileStore fans out its descent), so every mutation is interlocked.
    private int _tested;
    private int _maskRejected;
    private int _dateRejected;
    private int _prunedDirectories;
    private int _undated;

    // The span of dates the date-rejected files carried, so an empty selection can say what the files are dated
    // and not only that the window excluded them. Two values move together, so they are guarded by a lock rather
    // than interlocked; it is taken only for a rejected file, never for one the run reads.
    private readonly Lock _rejectedDatesLock = new();
    private DateTime? _earliestRejected;
    private DateTime? _latestRejected;

    public FileDateFilter(FileDateSpec? spec, DateTime? from, DateTime? to, DateTime? after, Regex? pathMask)
    {
        _spec = spec;
        _from = from;
        _to = to;
        _after = after;
        _pathMask = pathMask;
    }

    /// <summary>True when at least one of the filters is active; a filter with nothing to enforce is not built.</summary>
    public bool IsActive => _spec is not null || _from is not null || _to is not null || _after is not null || _pathMask is not null;

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
                latest);
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

        if (!HasWindow)
        {
            return true;
        }

        bool inWindow;
        DateTime? fileDate;
        if (_spec is null)
        {
            var modified = (file.Modified ?? DateTimeOffset.UtcNow).UtcDateTime;
            inWindow = Overlaps(new DateInterval(modified, modified));
            fileDate = modified;
        }
        else
        {
            var text = _spec.Source == FileDateSource.Path ? file.Path : file.Name;
            var interval = _spec.Extract(text);

            // A file that carries no parsable date is not part of a date-scoped selection: exclude it rather than
            // guess. With no window set (HasWindow false) we returned true above, so an undated file still loads then.
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
