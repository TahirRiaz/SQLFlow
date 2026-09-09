using SqlFlow.Core;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Schema;

/// <summary>Settings for the key-match (deleted-row detection) script. Column names are TARGET names.</summary>
public sealed record MatchKeyScriptOptions
{
    /// <summary>The match keys, as target column names.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    public MatchKeyAction Action { get; init; } = MatchKeyAction.Tag;

    /// <summary>Skip the action (and report the breach) when more than this percentage of the target would be
    /// affected.</summary>
    public int ActionThresholdPercent { get; init; } = 20;

    /// <summary>Tag mode only: tag a row only when its <see cref="DateColumn"/> is within this many months.</summary>
    public int? IgnoreDeletedRowsAfterMonths { get; init; }

    /// <summary>The target date column for the ignore window.</summary>
    public string? DateColumn { get; init; }

    /// <summary>A predicate bounding which target rows the pass may touch (raw-append contract: begins with
    /// <c>" AND "</c>), or null.</summary>
    public string? TargetFilter { get; init; }

    /// <summary>The soft-delete stamp column (DeletedDate_DW). Required for Tag mode.</summary>
    public string? DeletedDateColumn { get; init; }

    /// <summary>The row-status column (RowStatus_DW), stamped 'D' on tag and reset 'U' on resurrect; null to skip.</summary>
    public string? RowStatusColumn { get; init; }
}

/// <summary>The counters the key-match script reports as its single result row.</summary>
public sealed record MatchKeyCounters
{
    /// <summary>Total rows in the target before the action.</summary>
    public long TotalRows { get; init; }

    /// <summary>Target rows whose keys are missing from the source key set (within the filters/window).</summary>
    public long CandidateRows { get; init; }

    /// <summary>Rows actually tagged or deleted (0 when the threshold was breached).</summary>
    public long AffectedRows { get; init; }

    /// <summary>Tag mode: previously tagged rows whose keys reappeared in the source, now un-tagged.</summary>
    public long ResurrectedRows { get; init; }

    /// <summary>True when the candidate percentage exceeded the threshold and the action was skipped.</summary>
    public bool ThresholdBreached { get; init; }
}

/// <summary>
/// Generates the key-match (deleted-row detection) script: the target is anti-joined against the flow's
/// canonical key table holding the full distinct SOURCE key set (rebuilt per run by the ingestion runner),
/// and a target row whose key vanished from the source is
/// tagged (DeletedDate_DW, plus RowStatus_DW 'D') or hard-deleted, per the flow's policy. The comparison is
/// NULL-safe (a NULL key matches a NULL key, as the legacy string comparison did). Improvements over the
/// legacy client-side engine, deliberate: the comparison happens in SQL (no stream-misalignment false
/// positives), the action threshold is actually enforced and a breach is reported (legacy carried the column
/// but hardcoded 20 and skipped silently), and a tagged row whose key REAPPEARS in the source is un-tagged
/// (legacy never cleared DeletedDate_DW). One script, one result row of counters. Pure text generation.
/// </summary>
public static class MatchKeyGenerator
{
    public static string Generate(RelationalObject target, RelationalObject keyTable, MatchKeyScriptOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyTable);
        ArgumentNullException.ThrowIfNull(options);

        if (options.KeyColumns.Count == 0)
        {
            throw new SqlFlowException("The key-match pass requires at least one key column.");
        }

        if (options.ActionThresholdPercent is < 0 or > 100)
        {
            throw new SqlFlowException($"ActionThresholdPercent must be 0 to 100, got {options.ActionThresholdPercent}.");
        }

        if (options.Action == MatchKeyAction.Tag && string.IsNullOrWhiteSpace(options.DeletedDateColumn))
        {
            throw new SqlFlowException("Tag mode requires the DeletedDate column (enable the DeletedDate_DW system column).");
        }

        if (options.IgnoreDeletedRowsAfterMonths is not null && string.IsNullOrWhiteSpace(options.DateColumn))
        {
            throw new SqlFlowException("IgnoreDeletedRowsAfterMonths requires a date column.");
        }

        var trg = Qualify(target);
        var keys = Qualify(keyTable);
        var missing = $"NOT EXISTS (SELECT 1 FROM {keys} AS src WHERE {NullSafeKeyEquality(options.KeyColumns)})";
        var targetFilter = string.IsNullOrWhiteSpace(options.TargetFilter) ? string.Empty : " " + options.TargetFilter.Trim();

        string candidatePredicates;
        string actionStatement;
        if (options.Action == MatchKeyAction.Delete)
        {
            candidatePredicates = string.Empty;
            actionStatement = $"DELETE trg FROM {trg} AS trg WHERE {missing}{targetFilter};";
        }
        else
        {
            var deleted = Escape(options.DeletedDateColumn!);
            var window = options.IgnoreDeletedRowsAfterMonths is { } months
                ? $" AND trg.[{Escape(options.DateColumn!)}] >= DATEADD(MONTH, -{months}, SYSUTCDATETIME())"
                : string.Empty;
            candidatePredicates = $" AND trg.[{deleted}] IS NULL{window}";

            var stamps = $"trg.[{deleted}] = SYSUTCDATETIME()";
            if (!string.IsNullOrWhiteSpace(options.RowStatusColumn))
            {
                stamps += $", trg.[{Escape(options.RowStatusColumn!)}] = 'D'";
            }

            actionStatement =
                $"UPDATE trg SET {stamps} FROM {trg} AS trg WHERE {missing}{candidatePredicates}{targetFilter};";
        }

        // Tag mode also reconciles resurrections: a tagged row whose key is back in the source is un-tagged,
        // independent of the threshold (un-deleting is always safe).
        var resurrect = string.Empty;
        if (options.Action == MatchKeyAction.Tag)
        {
            var deleted = Escape(options.DeletedDateColumn!);
            var resurrectStamps = $"trg.[{deleted}] = NULL";
            if (!string.IsNullOrWhiteSpace(options.RowStatusColumn))
            {
                resurrectStamps += $", trg.[{Escape(options.RowStatusColumn!)}] = 'U'";
            }

            resurrect = $"""

                UPDATE trg SET {resurrectStamps}
                FROM {trg} AS trg
                WHERE trg.[{deleted}] IS NOT NULL
                  AND EXISTS (SELECT 1 FROM {keys} AS src WHERE {NullSafeKeyEquality(options.KeyColumns)}){targetFilter};
                SET @Resurrected = @@ROWCOUNT;
                """;
        }

        return $"""
            SET NOCOUNT ON;
            DECLARE @Total bigint = 0, @Candidates bigint = 0, @Affected bigint = 0, @Resurrected bigint = 0, @Breached bit = 0;
            SELECT @Total = COUNT_BIG(*) FROM {trg};
            SELECT @Candidates = COUNT_BIG(*) FROM {trg} AS trg WHERE {missing}{candidatePredicates}{targetFilter};
            IF @Total > 0 AND @Candidates * 100.0 / @Total > {options.ActionThresholdPercent}
                SET @Breached = 1;
            IF @Breached = 0 AND @Candidates > 0
            BEGIN
                {actionStatement}
                SET @Affected = @@ROWCOUNT;
            END;
            {resurrect}
            SELECT @Total AS TotalRows, @Candidates AS CandidateRows, @Affected AS AffectedRows, @Resurrected AS ResurrectedRows, @Breached AS ThresholdBreached;
            """;
    }

    // NULL-safe key equality: a NULL key matches a NULL key (the legacy string comparison treated NULLs as
    // equal), so a NULL-keyed target row is never reported missing against a NULL-keyed source row.
    private static string NullSafeKeyEquality(IReadOnlyList<string> keys)
        => string.Join(" AND ", keys.Select(k =>
            $"(src.[{Escape(k)}] = trg.[{Escape(k)}] OR (src.[{Escape(k)}] IS NULL AND trg.[{Escape(k)}] IS NULL))"));

    private static string Qualify(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
