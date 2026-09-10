using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// The format-agnostic settings the shared file-ingestion pipeline needs: which files to read, which
/// provenance/key columns to inject, the streaming caps, and the post-load file lifecycle. Each format
/// reader maps its own rich metadata (CSV, XLS, ...) onto this common shape.
/// </summary>
public sealed record FileSourceOptions
{
    // --- File selection ---
    public required string SrcPath { get; init; }
    public string? SrcFile { get; init; }
    public string? SrcPathMask { get; init; }
    public bool SearchSubDirectories { get; init; }
    public string? InitFromFileDate { get; init; }
    public string? InitToFileDate { get; init; }
    public string? IncrementalAfterDate { get; init; }

    /// <summary>
    /// Where the date window reads a file's business date: null keeps the legacy modified-timestamp behavior; a
    /// present spec reads the date from the path (Hive partitions) or the file name, which lets discovery prune
    /// whole out-of-window partition folders instead of enumerating the whole lake.
    /// </summary>
    public FileDateSpec? FileDate { get; init; }

    /// <summary>
    /// How <c>DataSet_DW</c> is derived: from a date detected in the file name (with last-modified as the
    /// fallback), or the last-modified timestamp only. Defaults to <see cref="DataSetDateSpec.ModifiedOnly"/>;
    /// each reader replaces it with <see cref="DataSetDateSpec.FromOptions"/> so a flow's <c>dataSetFromFileName</c>
    /// / <c>dataSetFormats</c> options take effect.
    /// </summary>
    public DataSetDateSpec DataSetDate { get; init; } = DataSetDateSpec.ModifiedOnly;

    // --- Post-load lifecycle ---
    public string? CopyToPath { get; init; }
    public string? ZipToPath { get; init; }
    public bool SrcDeleteIngested { get; init; }
    public bool SrcDeleteAtPath { get; init; }

    // --- Provenance columns ---
    public bool ShowPathWithFileName { get; init; }
    public bool IncludeFileName { get; init; } = true;
    public bool IncludeFileDate { get; init; } = true;
    public bool IncludeFileRowDate { get; init; } = true;
    public bool IncludeFileSize { get; init; } = true;
    public bool IncludeDataSet { get; init; } = true;
    public bool IncludeRowNumber { get; init; } = true;
    public bool IncludeFileLineNumber { get; init; }

    // --- Synthetic keys ---
    public bool IncludeHashKey { get; init; }
    public string? HashKeyColumns { get; init; }
    public string HashKeyType { get; init; } = "SHA2_512";
    public bool IncludeConcatKey { get; init; }
    public string? ConcatKeyColumns { get; init; }
    public string ConcatKeySeparator { get; init; } = "|";

    // --- Streaming caps (0 = unbounded; not all formats use these) ---
    public int MaxRows { get; init; }
    public int SkipEndingDataRows { get; init; }

    /// <summary>
    /// How many source files the flow keeps open at once: one plus the number opened ahead of the one being
    /// read. It buys latency, not parallel parsing. Rows must reach the loader in file order, so files are
    /// still consumed strictly one at a time; a higher value only means the next files' listing, schema read
    /// and download are already in flight when their turn comes, which is what a source of many small remote
    /// files spends most of its wall clock waiting on.
    /// <para>
    /// The cost is per open file, so the default is a property of the FORMAT (see
    /// <see cref="StreamingDefaultReadAhead"/> and <see cref="WholeFileDefaultReadAhead"/>) and the flow's
    /// <c>readAhead</c> option tunes it per source: raise it for many small files, lower it when each file is
    /// large enough that even a streaming reader's concurrent downloads are unwelcome.
    /// </para>
    /// </summary>
    public int ReadAhead { get; init; } = WholeFileDefaultReadAhead;

    /// <summary>
    /// The default for a format whose reader streams a file (CSV, JSON): an open file costs a small buffer and
    /// one record, so several can be in flight for the price of nothing much, and a source of many small
    /// remote files stops paying its per-file round trip serially.
    /// </summary>
    public const int StreamingDefaultReadAhead = 4;

    /// <summary>
    /// The default for a format whose reader must hold a whole file to read it (XLS and Parquet buffer a
    /// non-seekable stream in full; XML parses the whole document): every extra open file multiplies the peak,
    /// so these stay at one file at a time unless the flow's author, who knows how big the files are, says
    /// otherwise.
    /// </summary>
    public const int WholeFileDefaultReadAhead = 1;

    /// <summary>The most files any flow may hold open at once, whatever it asks for.</summary>
    public const int MaxReadAhead = 32;

    /// <summary>
    /// Reads the flow's <c>readAhead</c> option, falling back to the format's default. Rejecting an
    /// out-of-range value outright (rather than clamping it) keeps the flow definition honest: an author who
    /// wrote 200 gets told the ceiling instead of silently running something else.
    /// </summary>
    public static int ParseReadAhead(IReadOnlyDictionary<string, string?> options, int formatDefault)
    {
        ArgumentNullException.ThrowIfNull(options);
        var readAhead = options.GetInt("readAhead", formatDefault);
        if (readAhead < 1 || readAhead > MaxReadAhead)
        {
            throw new SqlFlowException(
                $"Invalid 'readAhead' value '{readAhead.ToString(CultureInfo.InvariantCulture)}'. "
                + $"Use 1 (read one file at a time) to {MaxReadAhead.ToString(CultureInfo.InvariantCulture)}.");
        }

        return readAhead;
    }
}

/// <summary>One parsed source row handed from a format reader to the shared pipeline.</summary>
/// <param name="Cells">
/// Values in the file's own column order (null for a missing cell). A string-typed format (CSV/XLS/JSON/XML)
/// fills these with strings; a self-describing format (Parquet) fills them with the real typed values that
/// match the column types it declared. The pipeline coerces each cell to its resolved target column type.
/// </param>
/// <param name="LineNumber">Physical line/row number in the file (for the FileLineNumber column).</param>
/// <param name="DataRowNumber">Data row index within the file (for the RowNumber_DW column).</param>
public readonly record struct FileLine(object?[] Cells, long LineNumber, long DataRowNumber);
