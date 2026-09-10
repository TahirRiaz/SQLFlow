using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ExcelDataReader;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Sources;

/// <summary>
/// Reads Excel workbooks (.xls / .xlsx) using <see cref="ExcelReaderFactory"/> (ExcelDataReader, the
/// same library the original SQLFlow used). The XLS metadata maps onto the library: SheetName /
/// UseSheetIndex pick the worksheet, SheetRange bounds the cells, FirstRowHasHeader names the columns.
/// Everything else (file selection, schema evolution, provenance/key columns, streaming, lifecycle)
/// comes from <see cref="FileSourceReaderBase"/>, so XLS rides the same code path as CSV.
/// </summary>
public sealed class XlsSourceReader : FileSourceReaderBase
{
    static XlsSourceReader()
    {
        // ExcelDataReader needs the legacy code pages for some .xls files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public XlsSourceReader(IFileLifecycle lifecycle, IEnumerable<IFileStore> fileStores)
        : base(lifecycle, fileStores)
    {
    }

    public override bool CanHandle(string sourceType)
        => sourceType is not null && (sourceType.Equals("xls", StringComparison.OrdinalIgnoreCase) || sourceType.Equals("xlsx", StringComparison.OrdinalIgnoreCase));

    protected override string DefaultFilePattern => "*.xlsx";

    protected override FileSourceOptions ReadOptions(SourceSpec source)
    {
        var meta = PreIngestionXls.FromSource(source);
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
            ReadAhead = FileSourceOptions.ParseReadAhead(source.Options, FileSourceOptions.WholeFileDefaultReadAhead),
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
        };
    }

    protected override async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IFileStore store, FileRef file, SourceSpec source, CancellationToken ct)
    {
        var meta = PreIngestionXls.FromSource(source);
        await using var stream = await OpenSeekableAsync(store, file, ct).ConfigureAwait(false);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        SelectSheet(reader, meta, file);
        var bounds = ParseRange(meta.SheetRange);

        if (!SkipRows(reader, bounds.FirstRow) || !reader.Read())
        {
            return [];
        }

        var cells = RowCells(reader, bounds);
        var names = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var header = meta.FirstRowHasHeader ? cells[i] : null;
            names[i] = string.IsNullOrEmpty(header) ? GeneratedColumnName(i) : header;
        }

        return names;
    }

    protected override async IAsyncEnumerable<FileLine> ReadLinesAsync(
        IFileStore store, FileRef file, SourceSpec source, [EnumeratorCancellation] CancellationToken ct)
    {
        var meta = PreIngestionXls.FromSource(source);
        await using var stream = await OpenSeekableAsync(store, file, ct).ConfigureAwait(false);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        SelectSheet(reader, meta, file);
        var bounds = ParseRange(meta.SheetRange);

        if (!SkipRows(reader, bounds.FirstRow))
        {
            yield break;
        }

        // rowIndex is the 0-based index (from the sheet top) of the row last read.
        var rowIndex = bounds.FirstRow - 1;

        if (meta.FirstRowHasHeader)
        {
            if (!reader.Read())
            {
                yield break;
            }

            rowIndex++;
        }

        long dataRow = 0;
        while (reader.Read())
        {
            rowIndex++;
            if (bounds.LastRow != int.MaxValue && rowIndex > bounds.LastRow)
            {
                break;
            }

            dataRow++;
            yield return new FileLine(RowCells(reader, bounds), rowIndex + 1, dataRow);
        }
    }

    private static async Task<Stream> OpenSeekableAsync(IFileStore store, FileRef file, CancellationToken ct)
    {
        var stream = await store.OpenReadAsync(file, ct).ConfigureAwait(false);
        if (stream.CanSeek)
        {
            return stream;
        }

        // ExcelDataReader needs random access (zip/BIFF); buffer non-seekable (cloud) streams.
        var buffer = new MemoryStream();
        await using (stream.ConfigureAwait(false))
        {
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static void SelectSheet(IExcelDataReader reader, PreIngestionXls meta, FileRef file)
    {
        if (string.IsNullOrWhiteSpace(meta.SheetName))
        {
            return; // the first worksheet
        }

        if (meta.UseSheetIndex)
        {
            if (!int.TryParse(meta.SheetName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                throw new SqlFlowException($"Invalid sheet index '{meta.SheetName}'.");
            }

            for (var i = 0; i < index; i++)
            {
                if (!reader.NextResult())
                {
                    throw new SqlFlowException($"Sheet index {index} not found in '{file.Name}'.");
                }
            }

            return;
        }

        do
        {
            if (string.Equals(reader.Name, meta.SheetName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        while (reader.NextResult());

        throw new SqlFlowException($"Sheet '{meta.SheetName}' not found in '{file.Name}'.");
    }

    private static bool SkipRows(IExcelDataReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!reader.Read())
            {
                return false;
            }
        }

        return true;
    }

    private static string?[] RowCells(IExcelDataReader reader, SheetBounds bounds)
    {
        var firstCol = bounds.FirstCol;
        var lastCol = bounds.LastCol == int.MaxValue ? reader.FieldCount - 1 : Math.Min(bounds.LastCol, reader.FieldCount - 1);
        if (lastCol < firstCol)
        {
            return [];
        }

        var cells = new string?[lastCol - firstCol + 1];
        for (var c = firstCol; c <= lastCol; c++)
        {
            cells[c - firstCol] = Cell(reader.GetValue(c));
        }

        return cells;
    }

    private static string? Cell(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static SheetBounds ParseRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return new SheetBounds(0, int.MaxValue, 0, int.MaxValue);
        }

        var parts = range.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (firstRow, firstCol) = ParseCell(parts[0]);
        if (parts.Length == 1)
        {
            return new SheetBounds(firstRow, int.MaxValue, firstCol, int.MaxValue);
        }

        var (secondRow, secondCol) = ParseCell(parts[1]);
        return new SheetBounds(
            Math.Min(firstRow, secondRow),
            Math.Max(firstRow, secondRow),
            Math.Min(firstCol, secondCol),
            Math.Max(firstCol, secondCol));
    }

    private static (int Row, int Col) ParseCell(string cell)
    {
        var split = 0;
        while (split < cell.Length && char.IsLetter(cell[split]))
        {
            split++;
        }

        var letters = cell[..split];
        var digits = cell[split..];
        if (letters.Length == 0 || !int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowNumber) || rowNumber < 1)
        {
            throw new SqlFlowException($"Invalid cell reference '{cell}' in sheetRange (use e.g. A1:D100).");
        }

        var col = 0;
        foreach (var ch in letters.ToUpperInvariant())
        {
            col = (col * 26) + (ch - 'A' + 1);
        }

        return (rowNumber - 1, col - 1);
    }

    private readonly record struct SheetBounds(int FirstRow, int LastRow, int FirstCol, int LastCol);
}
