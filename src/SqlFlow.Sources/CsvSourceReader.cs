using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using GenericParsing;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Reads delimited / fixed-width text files using <see cref="GenericParser"/> (the same library the
/// original SQLFlow used). The only format-specific concern is how a CSV file's columns and rows are
/// parsed; everything else (file selection, schema evolution, provenance/key columns, streaming, file
/// lifecycle) comes from <see cref="FileSourceReaderBase"/>, so every format shares one code path.
/// </summary>
public sealed class CsvSourceReader : FileSourceReaderBase
{
    public CsvSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => string.Equals(sourceType, "csv", StringComparison.OrdinalIgnoreCase);

    protected override string DefaultFilePattern => "*.csv";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionCsv.FromSource(source);
        return new FileSourceOptions
        {
            SrcPath = meta.SrcPath,
            SrcFile = meta.SrcFile,
            SrcPathMask = meta.SrcPathMask,
            SearchSubDirectories = meta.SearchSubDirectories,
            InitFromFileDate = meta.InitFromFileDate,
            InitToFileDate = meta.InitToFileDate,
            IncrementalAfterDate = meta.IncrementalAfterDate,
            FileDate = FileDateSpec.FromOptions(source.Options),
            DataSetDate = DataSetDateSpec.FromOptions(source.Options),
            ReadAhead = FileSourceOptions.ParseReadAhead(source.Options, FileSourceOptions.StreamingDefaultReadAhead),
            CopyToPath = meta.CopyToPath,
            ZipToPath = meta.ZipToPath,
            SrcDeleteIngested = meta.SrcDeleteIngested,
            SrcDeleteAtPath = meta.SrcDeleteAtPath,
            ShowPathWithFileName = meta.ShowPathWithFileName,
            IncludeFileName = meta.IncludeFileName,
            IncludeFileDate = meta.IncludeFileDate,
            IncludeFileRowDate = meta.IncludeFileRowDate,
            IncludeFileSize = meta.IncludeFileSize,
            IncludeDataSet = meta.IncludeDataSet,
            IncludeRowNumber = meta.IncludeRowNumber,
            IncludeFileLineNumber = meta.IncludeFileLineNumber,
            IncludeHashKey = meta.IncludeHashKey,
            HashKeyColumns = meta.HashKeyColumns,
            HashKeyType = meta.HashKeyType,
            IncludeConcatKey = meta.IncludeConcatKey,
            ConcatKeyColumns = meta.ConcatKeyColumns,
            ConcatKeySeparator = meta.ConcatKeySeparator,
            MaxRows = meta.MaxRows,
            SkipEndingDataRows = meta.SkipEndingDataRows,
        };
    }

    /// <summary>
    /// Reads one file's column names. The fast path reads the first row and resolves names from it. A
    /// header-only file (a header line with no data rows) is a valid empty dataset with a known schema,
    /// but <see cref="GenericParser"/> exposes no columns when no data row follows; in that case the
    /// first line is re-read as data and its values become the column names.
    /// </summary>
    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionCsv.FromSource(source);

        if (!meta.FirstRowHasHeader)
        {
            return await ReadHeaderlessColumnNamesAsync(store, file, meta, ct).ConfigureAwait(false);
        }

        await using (var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, ResolveEncoding(meta.SrcEncoding)))
        using (var parser = Configure(new GenericParser(), reader, meta))
        {
            TryReadRow(parser, file);
            if (parser.ColumnCount > 0)
            {
                return ResolveColumnNames(parser, meta);
            }
        }

        await using var headerStream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var headerReader = new StreamReader(headerStream, ResolveEncoding(meta.SrcEncoding));
        using var headerParser = Configure(new GenericParser(), headerReader, meta);
        headerParser.FirstRowHasHeader = false;
        if (!TryReadRow(headerParser, file))
        {
            return [];
        }

        var headerNames = new string[headerParser.ColumnCount];
        for (var i = 0; i < headerParser.ColumnCount; i++)
        {
            headerNames[i] = headerParser[i] ?? string.Empty;
        }

        return headerNames;
    }

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionCsv.FromSource(source);

        await using var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, ResolveEncoding(meta.SrcEncoding));
        using var parser = Configure(new GenericParser(), reader, meta);

        while (true)
        {
            FileLine line;

            // The per-row try only guards parse + value extraction (no yield inside it, which an
            // iterator forbids). On failure parser.DataRowNumber pins down the offending row.
            try
            {
                if (!parser.Read())
                {
                    break;
                }

                // GenericParser, on a file whose final line carries no trailing terminator, can surface one
                // last record that holds no fields at all (ColumnCount == 0) once the content is exhausted.
                // For a header-only file with no trailing newline this is the only "row" it emits, and the
                // pipeline would otherwise stamp it as a spurious all-NULL data row. The same file WITH a
                // trailing newline never produces it. A real final row (even one of only whitespace, only
                // empty fields, or a lone trailing delimiter) always parses to at least one field, so this
                // is the synthetic end-of-file artifact, not data: skip it and keep reading.
                if (parser.ColumnCount == 0)
                {
                    continue;
                }

                var cells = new string?[parser.ColumnCount];
                for (var i = 0; i < parser.ColumnCount; i++)
                {
                    cells[i] = parser[i];
                }

                line = new FileLine(cells, parser.FileRowNumber, parser.DataRowNumber);
            }
            catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
            {
                throw ParseError(parser, file, ex);
            }

            yield return line;
        }
    }

    /// <summary>
    /// Names the columns of a headerless file. Such a file names its columns positionally
    /// (<c>Column1..ColumnN</c>), so N is the width of its WIDEST row, not of its first. Ragged files are
    /// normal in this shape: the settlement export interleaves record types of different widths, and its
    /// first row is a narrow 28-field header record while the transaction rows that follow carry 30. Sizing
    /// the schema from row one would drop every cell past the 28th, silently losing two columns of every
    /// transaction. So the whole file is scanned once for its maximum width. That costs one extra sequential
    /// parse of a file the run is about to parse anyway, and it is the only width that cannot be wrong: any
    /// cap would mis-size a file whose widest row arrives late.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadHeaderlessColumnNamesAsync(
        IFileStore store, FileRef file, PreIngestionCsv meta, CancellationToken ct)
    {
        var width = 0;

        await using (var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false))
        using (var reader = new StreamReader(stream, ResolveEncoding(meta.SrcEncoding)))
        using (var parser = Configure(new GenericParser(), reader, meta))
        {
            while (TryReadRow(parser, file))
            {
                ct.ThrowIfCancellationRequested();
                if (parser.ColumnCount > width)
                {
                    width = parser.ColumnCount;
                }
            }
        }

        var names = new string[width];
        for (var i = 0; i < width; i++)
        {
            names[i] = GeneratedColumnName(i);
        }

        return names;
    }

    private static string[] ResolveColumnNames(GenericParser parser, PreIngestionCsv meta)
    {
        var names = new string[parser.ColumnCount];
        for (var i = 0; i < parser.ColumnCount; i++)
        {
            var header = meta.FirstRowHasHeader ? parser.GetColumnName(i) : null;
            names[i] = string.IsNullOrEmpty(header) ? GeneratedColumnName(i) : header;
        }

        return names;
    }

    private static bool TryReadRow(GenericParser parser, FileRef file)
    {
        try
        {
            return parser.Read();
        }
        catch (Exception ex) when (ex is not SqlFlowException and not OperationCanceledException)
        {
            throw ParseError(parser, file, ex);
        }
    }

    private static SqlFlowException ParseError(GenericParser parser, FileRef file, Exception inner)
        => new($"Parse error in '{file.Name}' near file line {parser.FileRowNumber + 1}: {inner.Message}", inner);

    private static GenericParser Configure(GenericParser parser, TextReader reader, PreIngestionCsv meta)
    {
        parser.SetDataSource(reader);
        parser.FirstRowHasHeader = meta.FirstRowHasHeader;
        parser.TrimResults = meta.TrimResults;
        parser.StripControlChars = meta.StripControlChars;
        parser.SkipStartingDataRows = meta.SkipStartingDataRows;
        parser.MaxBufferSize = Math.Max(meta.MaxBufferSize, 1024);
        parser.FirstRowSetsExpectedColumnCount = meta.FirstRowSetsExpectedColumnCount;

        if (meta.ExpectedColumnCount > 0)
        {
            parser.ExpectedColumnCount = meta.ExpectedColumnCount;
        }

        if (!string.IsNullOrWhiteSpace(meta.ColumnWidths))
        {
            parser.ColumnWidths = ParseWidths(meta.ColumnWidths);
        }
        else
        {
            parser.ColumnDelimiter = DelimiterChar(meta.ColumnDelimiter);
        }

        if (!string.IsNullOrEmpty(meta.TextQualifier))
        {
            parser.TextQualifier = meta.TextQualifier[0];
        }

        if (!string.IsNullOrEmpty(meta.EscapeCharacter) && meta.EscapeCharacter != "0")
        {
            parser.EscapeCharacter = meta.EscapeCharacter[0];
        }

        if (!string.IsNullOrEmpty(meta.CommentCharacter))
        {
            parser.CommentCharacter = meta.CommentCharacter[0];
        }

        return parser;
    }

    private static char DelimiterChar(string? delimiter) => delimiter switch
    {
        null or "" => ',',
        "\\t" or "\t" => '\t',
        _ => delimiter[0],
    };

    private static int[] ParseWidths(string widths) => widths
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(w => int.Parse(w, CultureInfo.InvariantCulture))
        .ToArray();

    /// <summary>
    /// Resolves the declared source encoding. Beyond the Unicode family it accepts any code page .NET knows by
    /// name or number (Latin1/ISO-8859-1, windows-1252, 1252, ...), which is what legacy CSV feeds are delivered
    /// in: the settlement files are Latin1, and reading them as UTF-8 mangles every Norwegian character.
    /// An encoding the runtime cannot resolve is an authoring error, so it fails loudly rather than silently
    /// falling back to UTF-8 and corrupting the landed text.
    /// </summary>
    private static Encoding ResolveEncoding(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Encoding.UTF8;
        }

        switch (trimmed.ToUpperInvariant())
        {
            case "UTF8" or "UTF-8":
                return Encoding.UTF8;
            case "ASCII":
                return Encoding.ASCII;
            case "UNICODE" or "UTF16" or "UTF-16":
                return Encoding.Unicode;
            case "UTF32" or "UTF-32":
                return Encoding.UTF32;
            case "LATIN1" or "LATIN-1" or "ISO-8859-1" or "ISO8859-1":
                return Encoding.Latin1;
        }

        // The single-byte code pages (windows-1252 and friends) live in the code-page provider, which is not
        // registered by default on .NET Core; registering is idempotent and cheap.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var codePage)
                ? Encoding.GetEncoding(codePage)
                : Encoding.GetEncoding(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new SqlFlowException($"Unknown source encoding '{trimmed}'. Use a .NET encoding name or code page number (for example utf-8, Latin1, windows-1252, 1252).", ex);
        }
    }
}
