using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Export.Legacy;

/// <summary>
/// Maps a legacy <c>flw.Export</c> row into the V3 <see cref="ExportFlow"/> model. Pure and lossless: every
/// column has a destination, a legacy NULL resolves to the documented table default, and a missing required
/// value (SysAlias / srcServer / srcDBSchTbl) fails fast with a precise, flow-identified error. The Azure
/// credential columns collapse into the <c>ServicePrincipalAlias</c> registry alias (the secretless decision).
/// </summary>
public static class ExportFlowMapper
{
    public static ExportFlow FromLegacy(LegacyExportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ExportFlow
        {
            FlowId = row.FlowID,
            Batch = NullIfBlank(row.Batch),
            SysAlias = Require(row.SysAlias, row.FlowID, "SysAlias"),
            SrcServer = Require(row.srcServer, row.FlowID, "srcServer"),
            Source = ParseObject(row.srcDBSchTbl, row.FlowID, "srcDBSchTbl"),
            SrcWithHint = NullIfBlank(row.srcWithHint),
            SrcFilter = NullIfBlank(row.srcFilter),
            // Legacy stored these bracketed ([CalendarID]); the planner brackets what it is given, so they are
            // unbracketed once here rather than producing [[CalendarID]]] in every generated SELECT.
            IncrementalColumn = Column(row.IncrementalColumn),
            DateColumn = Column(row.DateColumn) ?? DateChunkColumn(row),
            NoOfOverlapDays = row.NoOfOverlapDays ?? 1,
            FromDate = ToDateOnly(row.FromDate),
            ToDate = ToDateOnly(row.ToDate),
            ExportBy = (NullIfBlank(row.ExportBy) ?? "D").Trim().ToUpperInvariant(),
            ExportSize = row.ExportSize ?? 1,
            TargetReference = row.ServicePrincipalAlias is { } alias && !string.IsNullOrWhiteSpace(alias) ? "@" + alias.Trim() : null,
            TrgPath = NullIfBlank(row.trgPath),
            TrgFileName = NullIfBlank(row.trgFileName),
            TrgFiletype = (NullIfBlank(row.trgFiletype) ?? "csv").Trim(),
            // A row from the legacy control database describes a flow whose files a consumer is already parsing,
            // so it keeps the bytes the legacy engine wrote: CsvHelper under the invariant culture, and a UTF-8
            // byte-order mark, which its cloud writer emitted unconditionally regardless of trgEncoding.
            TrgEncoding = NullIfBlank(row.trgEncoding) ?? "UTF8BOM",
            TrgValueFormat = ExportValueFormat.Legacy,
            CompressionType = NullIfBlank(row.CompressionType) ?? "gzip",
            ColumnDelimiter = NullIfBlank(row.ColumnDelimiter) ?? ";",
            TextQualifier = NullIfBlank(row.TextQualifier) ?? "\"",
            AddTimeStampToFileName = row.AddTimeStampToFileName ?? true,
            Subfolderpattern = NullIfBlank(row.Subfolderpattern),
            NoOfThreads = row.NoOfThreads ?? 0,
            ZipTrg = row.ZipTrg ?? false,
            OnErrorResume = row.OnErrorResume ?? true,
            PostInvokeAlias = NullIfBlank(row.PostInvokeAlias),
            DeactivateFromBatch = row.DeactivateFromBatch ?? false,
            FlowType = NullIfBlank(row.FlowType) ?? "exp",
            FromObjectMK = row.FromObjectMK,
            ToObjectMK = row.ToObjectMK,
            CreatedBy = NullIfBlank(row.CreatedBy),
            CreatedDate = row.CreatedDate,
        };
    }

    private static string Require(string? value, int flowId, string column)
        => NullIfBlank(value) ?? throw new SqlFlowException($"Export flow {flowId} is missing the required '{column}'.");

    private static RelationalObject ParseObject(string? name, int flowId, string column)
    {
        var value = NullIfBlank(name)
            ?? throw new SqlFlowException($"Export flow {flowId} is missing the required '{column}'.");
        try
        {
            return RelationalObject.Parse(value);
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"Export flow {flowId}, column '{column}': {ex.Message}", ex);
        }
    }

    private static DateOnly? ToDateOnly(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Column(string? value)
        => NullIfBlank(value) is { } name ? IngestionText.Unbracket(name) : null;

    /// <summary>
    /// The date a day/month export chunks on when the row leaves <c>DateColumn</c> empty. Legacy's ExecExport
    /// fell through to a date-typed <c>IncrementalColumn</c> in exactly that case, and the delivery flows shaped
    /// like that (an export windowed on its own CalendarID) rely on it; without the fallback the planner rejects
    /// the flow for having no DateColumn. Day/month chunking is a date interval by definition, so the incremental
    /// column IS that date here; an integer key chunks under ExportBy 'K' instead and is left alone.
    /// </summary>
    private static string? DateChunkColumn(LegacyExportRow row)
    {
        var by = (NullIfBlank(row.ExportBy) ?? "D").Trim().ToUpperInvariant();
        return by is "D" or "M" ? Column(row.IncrementalColumn) : null;
    }
}
