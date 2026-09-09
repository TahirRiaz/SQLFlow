using System.Globalization;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for <see cref="ParquetSourceReader"/> over real temp
/// .parquet files (no database). These cases harden boundaries the primary reader tests do not exercise:
/// the full integer/float/decimal/Guid/binary type-mapping table and the unsigned-widening of cell values,
/// logical types that Parquet.Net 5.6.1 surfaces as a different CLR type than they were written as (a date
/// reads back as DateTime, a time-of-day as TimeSpan), int96/Impala timestamps, decimal precision/scale
/// boundaries (a 38,0 value at full precision and a precision clamped from 50 to 38), an all-null column,
/// an empty file, a single row, projecting a column subset through OpenAsync, provenance
/// toggles and path-qualified file names, deterministic file-size/file-date provenance values, exact JSON
/// escaping and value shapes for scalar/bool/double/decimal lists and maps (null values, duplicate keys),
/// nested struct flattening more than one level deep, legacy column-name cleanup of invalid characters with
/// the Norwegian letters preserved, ordinal de-collision of identically named flat columns, nested columns
/// across multiple row groups, and the location / no-files-matched error paths.
/// </summary>
public sealed class ParquetEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_prqedge_" + Guid.NewGuid().ToString("N"));
    private readonly ParquetSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public ParquetEdgeCaseTests() => Directory.CreateDirectory(_dir);

    private string PathFor(string name) => Path.Combine(_dir, name);

    private static SourceSpec EdgeSource(string locationFileOrDir, Dictionary<string, string?>? options = null)
        => new() { Type = "parquet", Location = locationFileOrDir, Options = options ?? new Dictionary<string, string?>() };

    /// <summary>Writes one row group, one DataColumn per leaf, exactly as the primary tests do.</summary>
    private static async Task WriteOneGroupAsync(string path, ParquetSchema schema, params Array[] columns)
    {
        var leaves = schema.GetDataFields();
        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rg = writer.CreateRowGroup();
        for (var i = 0; i < leaves.Length; i++)
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[i], columns[i]));
        }
    }

    /// <summary>Writes one row group where some leaves carry explicit repetition levels (nested columns).</summary>
    private static async Task WriteRepAsync(string path, ParquetSchema schema, params (Array Data, int[]? Rep)[] columns)
    {
        var leaves = schema.GetDataFields();
        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        using var rg = writer.CreateRowGroup();
        for (var i = 0; i < leaves.Length; i++)
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[i], columns[i].Data, columns[i].Rep));
        }
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadAllRowsAsync(SourceSpec source)
    {
        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = new List<object?[]>();
        while (await data.ReadAsync())
        {
            var row = new object?[data.FieldCount];
            for (var i = 0; i < data.FieldCount; i++)
            {
                row[i] = data.IsDBNull(i) ? null : data.GetValue(i);
            }

            rows.Add(row);
        }

        return (columns.Select(c => c.Name).ToList(), rows);
    }

    private static object? CellOf(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    private async Task<string> SqlTypeOfAsync(SourceSpec source, string column)
    {
        var columns = await _reader.GetColumnsAsync(source);
        return columns.Single(c => c.Name == column).SqlType!;
    }

    // ---- Scalar type-mapping table (the integer/float/binary widths the primary 6-type test omits) ---------

    public static TheoryData<string, string, Type> ScalarTypeMappingData() => new()
    {
        { "i8", "smallint", typeof(short) },           // sbyte -> smallint
        { "u8", "tinyint", typeof(byte) },             // byte -> tinyint
        { "i16", "smallint", typeof(short) },          // short -> smallint
        { "u16", "int", typeof(int) },                 // ushort -> int (no SQL unsigned)
        { "u32", "bigint", typeof(long) },             // uint -> bigint
        { "f32", "real", typeof(float) },              // float -> real (distinct from double -> float)
        { "u64", "decimal(20,0)", typeof(decimal) },   // ulong -> decimal(20,0)
        { "g", "uniqueidentifier", typeof(Guid) },     // Guid -> uniqueidentifier
        { "bin", "varbinary(max)", typeof(byte[]) },   // byte[] -> varbinary(max)
    };

    [Theory]
    [MemberData(nameof(ScalarTypeMappingData))]
    public async Task ScalarTypes_MapToExpectedSqlTypeAndClrType(string kind, string expectedSql, Type expectedClr)
    {
        var path = PathFor($"scalarmap_{kind}.parquet");
        DataField field = kind switch
        {
            "i8" => new DataField<sbyte>(kind),
            "u8" => new DataField<byte>(kind),
            "i16" => new DataField<short>(kind),
            "u16" => new DataField<ushort>(kind),
            "u32" => new DataField<uint>(kind),
            "f32" => new DataField<float>(kind),
            "u64" => new DataField<ulong>(kind),
            "g" => new DataField<Guid>(kind),
            "bin" => new DataField<byte[]>(kind),
            _ => throw new InvalidOperationException($"Unhandled kind '{kind}'."),
        };

        Array data = kind switch
        {
            "i8" => new sbyte[] { 1 },
            "u8" => new byte[] { 1 },
            "i16" => new short[] { 1 },
            "u16" => new ushort[] { 1 },
            "u32" => new uint[] { 1U },
            "f32" => new float[] { 1f },
            "u64" => new ulong[] { 1UL },
            "g" => new[] { Guid.Parse("00000000-0000-0000-0000-000000000001") },
            "bin" => new[] { new byte[] { 1 } },
            _ => throw new InvalidOperationException($"Unhandled kind '{kind}'."),
        };

        await WriteOneGroupAsync(path, new ParquetSchema(field), data);

        var columns = await _reader.GetColumnsAsync(EdgeSource(path));
        var column = columns.Single(c => c.Name == kind);
        Assert.Equal(expectedSql, column.SqlType);
        Assert.Equal(expectedClr, column.Type);
    }

    // ---- Unsigned widening of the actual cell values (ConvertValue promotes to the next signed CLR type) ---

    [Fact]
    public async Task UnsignedValues_AreWidenedToTheirSignedClrType()
    {
        var sb = new DataField<sbyte>("sb");
        var us = new DataField<ushort>("us");
        var ui = new DataField<uint>("ui");
        var ul = new DataField<ulong>("ul");
        var path = PathFor("unsigned.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(sb, us, ui, ul),
            new sbyte[] { -5 },
            new ushort[] { 60000 },
            new uint[] { 4_000_000_000U },
            new ulong[] { 18_000_000_000_000_000_000UL });

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var row = Assert.Single(rows);

        Assert.Equal((short)-5, CellOf(cols, row, "sb"));         // sbyte -> short, sign preserved
        Assert.Equal(60000, CellOf(cols, row, "us"));             // ushort -> int
        Assert.Equal(4_000_000_000L, CellOf(cols, row, "ui"));    // uint -> long (overflows int)
        Assert.Equal(18_000_000_000_000_000_000m, CellOf(cols, row, "ul")); // ulong -> decimal (overflows long)
    }

    [Fact]
    public async Task ByteValue_StaysTinyintAndKeepsValueAtTheTop()
    {
        var b = new DataField<byte>("b");
        var path = PathFor("byteval.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(b), new byte[] { 0, 255 });

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));

        Assert.Equal((byte)0, CellOf(cols, rows[0], "b"));
        Assert.Equal((byte)255, CellOf(cols, rows[1], "b"));
    }

    // ---- float specials are a different ConvertValue arm than the double specials the primary test covers ---

    [Fact]
    public async Task FloatSpecials_NaNAndInfinity_BecomeNull_WhileFiniteSurvives()
    {
        var v = new DataField<float>("v");
        var path = PathFor("floatspecials.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(v),
            new[] { 1.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity });

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));

        Assert.Equal(1.5f, CellOf(cols, rows[0], "v"));
        Assert.Null(CellOf(cols, rows[1], "v"));   // NaN -> NULL (SQL real cannot represent it)
        Assert.Null(CellOf(cols, rows[2], "v"));   // +Infinity -> NULL
        Assert.Null(CellOf(cols, rows[3], "v"));   // -Infinity -> NULL
    }

    // ---- Logical types whose write CLR type differs from the read CLR type in Parquet.Net 5.6.1 ------------

    [Fact]
    public async Task DateLogicalType_ReadsBackAsDateTime_AndMapsToDatetime2()
    {
        // A Parquet 'date' (written from DateOnly) is surfaced by Parquet.Net 5.6.1 as a midnight DateTime, so
        // the reader maps it to datetime2 and yields a DateTime, not a DateOnly/'date'.
        var d = new DataField<DateOnly>("d");
        var path = PathFor("datelogical.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(d), new[] { new DateOnly(2024, 1, 15) });

        Assert.Equal("datetime2", await SqlTypeOfAsync(EdgeSource(path), "d"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var value = Assert.IsType<DateTime>(CellOf(cols, Assert.Single(rows), "d"));
        Assert.Equal(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [Fact]
    public async Task TimeOfDayLogicalType_ReadsBackAsTimeSpan_AndMapsToNvarcharMax()
    {
        // A Parquet time-of-day (written from TimeOnly) is surfaced as a TimeSpan by Parquet.Net 5.6.1, so the
        // reader takes the TimeSpan fallback: nvarchar(max), the duration rendered with the "c" format.
        var t = new DataField<TimeOnly>("t");
        var path = PathFor("timelogical.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(t), new[] { new TimeOnly(13, 45, 30) });

        Assert.Equal("nvarchar(max)", await SqlTypeOfAsync(EdgeSource(path), "t"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("13:45:30", CellOf(cols, Assert.Single(rows), "t"));
    }

    [Fact]
    public async Task Int96ImpalaTimestamp_MapsToDatetime2_AndPreservesTheUtcInstant()
    {
        var ts = new DateTimeDataField("ts", DateTimeFormat.Impala);
        var path = PathFor("int96.parquet");
        var instant = new DateTime(2024, 1, 15, 13, 45, 30, DateTimeKind.Utc);
        await WriteOneGroupAsync(path, new ParquetSchema(ts), new[] { instant });

        Assert.Equal("datetime2", await SqlTypeOfAsync(EdgeSource(path), "ts"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var value = Assert.IsType<DateTime>(CellOf(cols, Assert.Single(rows), "ts"));
        Assert.Equal(instant, value.ToUniversalTime());
    }

    [Fact]
    public async Task GuidColumn_RoundTripsAsTypedGuid()
    {
        var g = new DataField<Guid>("g");
        var path = PathFor("guid.parquet");
        var id = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        await WriteOneGroupAsync(path, new ParquetSchema(g), new[] { id });

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(id, CellOf(cols, Assert.Single(rows), "g"));
    }

    [Fact]
    public async Task BinaryColumn_RoundTripsBytes_IncludingEmptyArray()
    {
        var b = new DataField<byte[]>("b");
        var path = PathFor("binary.parquet");
        // A byte[][] is itself an Array[] under array covariance, so it must be wrapped as a single column arg
        // (otherwise the params spread would treat each byte[] as a separate column).
        var binaryColumn = new byte[][] { new byte[] { 1, 2, 3 }, Array.Empty<byte>() };
        await WriteOneGroupAsync(path, new ParquetSchema(b), new Array[] { binaryColumn });

        Assert.Equal("varbinary(max)", await SqlTypeOfAsync(EdgeSource(path), "b"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(CellOf(cols, rows[0], "b")));
        Assert.Empty(Assert.IsType<byte[]>(CellOf(cols, rows[1], "b")));
    }

    // ---- Decimal precision / scale boundaries -------------------------------------------------------------

    [Fact]
    public async Task DecimalAt38_0_IsNotClamped_AndCarriesAFullPrecisionValue()
    {
        var d = new DecimalDataField("d", 38, 0);
        var path = PathFor("dec380.parquet");
        const decimal big = 12345678901234567890123456789m;   // 29 digits, fits decimal(38,0)
        await WriteOneGroupAsync(path, new ParquetSchema(d), new[] { big });

        Assert.Equal("decimal(38,0)", await SqlTypeOfAsync(EdgeSource(path), "d"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(big, CellOf(cols, Assert.Single(rows), "d"));
    }

    [Fact]
    public async Task DecimalWithMidScale_KeepsItsScale()
    {
        var d = new DecimalDataField("d", 38, 10);
        var path = PathFor("dec3810.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(d), new decimal[] { 123.456m });

        Assert.Equal("decimal(38,10)", await SqlTypeOfAsync(EdgeSource(path), "d"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(123.456m, CellOf(cols, Assert.Single(rows), "d"));
    }

    [Fact]
    public async Task DecimalPrecision50_IsClampedTo38_PreservingScale()
    {
        // A producer may declare a precision SQL Server cannot hold (max 38). The schema read clamps the
        // precision to 38 while the (in-range) scale is preserved, and an in-range value round-trips.
        var d = new DecimalDataField("d", 50, 4);
        var path = PathFor("dec504.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(d), new decimal[] { 12.3456m });

        Assert.Equal("decimal(38,4)", await SqlTypeOfAsync(EdgeSource(path), "d"));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(12.3456m, CellOf(cols, Assert.Single(rows), "d"));
    }

    // ---- All-null, empty, and single-row shapes ---------------------------------------------------------------

    [Fact]
    public async Task AllNullColumn_YieldsEveryCellNull_ButKeepsItsDeclaredType()
    {
        var id = new DataField<int>("id");
        var note = new DataField<string>("note");
        var score = new DataField<int?>("score");
        var path = PathFor("allnull.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(id, note, score),
            new int[] { 1, 2, 3 },
            new string?[] { null, null, null },
            new int?[] { null, null, null });

        var columns = await _reader.GetColumnsAsync(EdgeSource(path));
        Assert.Equal(typeof(string), columns.Single(c => c.Name == "note").Type);
        Assert.Equal("int", columns.Single(c => c.Name == "score").SqlType);

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Null(CellOf(cols, r, "note")));
        Assert.All(rows, r => Assert.Null(CellOf(cols, r, "score")));
    }

    [Fact]
    public async Task EmptyFile_YieldsNoRows_ButStillExposesSourceAndSystemColumns()
    {
        var path = PathFor("empty.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id"), new DataField<string>("name")),
            Array.Empty<int>(), Array.Empty<string>());

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));

        Assert.Empty(rows);
        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("RowNumber_DW", columns);
        Assert.Contains("FileName_DW", columns);
    }

    [Fact]
    public async Task SingleRowFile_NumbersThatRowAsRowOne()
    {
        var path = PathFor("single.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id")), new int[] { 99 });

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var row = Assert.Single(rows);

        Assert.Equal(99, CellOf(columns, row, "id"));
        Assert.Equal(1L, CellOf(columns, row, "RowNumber_DW"));
    }

    // ---- Projecting a subset of columns through OpenAsync ------------------------------------------------------

    [Fact]
    public async Task OpenWithColumnSubset_EmitsOnlyTheRequestedColumns()
    {
        var id = new DataField<int>("id");
        var name = new DataField<string>("name");
        var amount = new DataField<int>("amount");
        var path = PathFor("subset.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(id, name, amount),
            new int[] { 1 }, new string?[] { "Ann" }, new int[] { 500 });

        var all = await _reader.GetColumnsAsync(EdgeSource(path));
        var subset = all.Where(c => c.Name is "id" or "amount").ToList();

        var read = await _reader.OpenAsync(EdgeSource(path), subset);
        await using var data = read.Reader;

        Assert.Equal(2, data.FieldCount);
        Assert.True(await data.ReadAsync());
        Assert.Equal(1, data.GetValue(data.GetOrdinal("id")));
        Assert.Equal(500, data.GetValue(data.GetOrdinal("amount")));
        Assert.Throws<IndexOutOfRangeException>(() => data.GetOrdinal("name"));
        Assert.False(await data.ReadAsync());
    }

    // ---- Provenance toggles and path-qualified file names -----------------------------------------------------

    [Fact]
    public async Task DisablingRowNumberProvenance_RemovesThatColumnButKeepsTheOthers()
    {
        var path = PathFor("noprov.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id")), new int[] { 1 });

        var columns = await _reader.GetColumnsAsync(
            EdgeSource(path, new Dictionary<string, string?> { ["includeRowNumber"] = "false" }));
        var names = columns.Select(c => c.Name).ToList();

        Assert.DoesNotContain("RowNumber_DW", names);
        Assert.Contains("FileName_DW", names);   // a different provenance column is still injected
        Assert.Contains("id", names);
    }

    [Fact]
    public async Task ShowPathWithFileName_PutsTheFullPathInTheFileNameColumn()
    {
        var path = PathFor("withpath.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id")), new int[] { 1 });

        var (columns, rows) = await ReadAllRowsAsync(
            EdgeSource(path, new Dictionary<string, string?> { ["showPathWithFileName"] = "true" }));

        var fileName = Assert.IsType<string>(CellOf(columns, Assert.Single(rows), "FileName_DW"));
        Assert.Equal(path, fileName);
        Assert.Contains(Path.DirectorySeparatorChar, fileName);
    }

    [Fact]
    public async Task FileSizeProvenance_MatchesTheActualFileLength()
    {
        var path = PathFor("sized.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id")), new int[] { 1, 2, 3 });
        var expectedSize = new FileInfo(path).Length;

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));

        // Provenance lands as strings in the raw layer; the size is the byte count rendered as digits.
        Assert.Equal(expectedSize.ToString(CultureInfo.InvariantCulture), CellOf(columns, rows[0], "FileSize_DW"));
    }

    [Fact]
    public async Task FileDateProvenance_MatchesTheFilesModifiedTimestamp()
    {
        var path = PathFor("dated.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("id")), new int[] { 1 });
        var stamp = new DateTime(2021, 6, 1, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));

        // Provenance lands as strings in the raw layer: the modified timestamp is encoded yyyyMMddHHmmss
        // (UTC), the shape the transformation view CASTs to decimal(14,0).
        var fileDate = Assert.IsType<string>(CellOf(columns, Assert.Single(rows), "FileDate_DW"));
        Assert.Equal(stamp.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), fileDate);
    }

    // ---- ExpectedColumnCount: the matching (non-throwing) side, counting leaves ------------------------------

    [Fact]
    public async Task ExpectedLeafColumnCount_MatchingAStruct_DoesNotThrow()
    {
        // The struct expands into two output columns, but ExpectedColumnCount is the leaf count: id + two leaves.
        var schema = new ParquetSchema(
            new DataField<int>("id"),
            new StructField("addr", new DataField<string>("city"), new DataField<string>("zip")));
        var path = PathFor("countok.parquet");
        await WriteOneGroupAsync(path, schema, new int[] { 1 }, new string?[] { "Oslo" }, new string?[] { "0001" });

        var columns = await _reader.GetColumnsAsync(
            EdgeSource(path, new Dictionary<string, string?> { ["expectedColumnCount"] = "3" }));

        Assert.Contains(columns, c => c.Name == "addr_city");
        Assert.Contains(columns, c => c.Name == "addr_zip");
    }

    // ---- Column-name cleanup (invalid characters -> underscore, Norwegian letters preserved) ----------------

    [Fact]
    public async Task InvalidCharactersInFieldNames_AreReplacedWithUnderscores()
    {
        var path = PathFor("dirtyname.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("First Name (USD)")), new int[] { 1 });

        var columns = await _reader.GetColumnsAsync(EdgeSource(path));

        // Every run of invalid characters maps one-to-one to an underscore: space, '(' and ')' each become '_'.
        Assert.Contains(columns, c => c.Name == "First_Name__USD_");
    }

    [Fact]
    public async Task NorwegianLettersInFieldNames_ArePreservedByTheCleanup()
    {
        var path = PathFor("norsk.parquet");
        await WriteOneGroupAsync(path, new ParquetSchema(new DataField<int>("blåbær"), new DataField<int>("ÆØÅ")),
            new int[] { 1 }, new int[] { 2 });

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));

        Assert.Contains("blåbær", columns);
        Assert.Contains("ÆØÅ", columns);
        Assert.Equal(1, CellOf(columns, Assert.Single(rows), "blåbær"));
    }

    [Fact]
    public async Task CleanupInducedCollisions_AreDeCollidedAndAllValuesSurvive()
    {
        // Three DISTINCT leaf names that the legacy cleanup collapses onto the same token ("Col_1"): the
        // ordinal-suffix de-collision keeps them separate, and because the underlying leaf paths differ, every
        // value is read into its own column (none is lost).
        var schema = new ParquetSchema(
            new DataField<string>("Col#1"), new DataField<string>("Col@1"), new DataField<string>("Col 1"));
        var path = PathFor("clash.parquet");
        await WriteOneGroupAsync(path, schema,
            new string?[] { "first" }, new string?[] { "second" }, new string?[] { "third" });

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var row = Assert.Single(rows);

        var collisionColumns = columns.Where(c => c.StartsWith("Col", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, collisionColumns.Count);
        var values = collisionColumns.Select(c => CellOf(columns, row, c)).ToList();
        Assert.Contains("first", values);
        Assert.Contains("second", values);
        Assert.Contains("third", values);
    }

    // ---- Nested struct flattening more than one level deep ---------------------------------------------------

    [Fact]
    public async Task NestedStruct_FlattensEveryLevelIntoDottedColumns()
    {
        var geo = new StructField("geo", new DataField<double>("lat"), new DataField<double>("lon"));
        var addr = new StructField("addr", geo, new DataField<string>("city"));
        var schema = new ParquetSchema(new DataField<int>("id"), addr);
        var path = PathFor("deepstruct.parquet");
        await WriteOneGroupAsync(path, schema,
            new int[] { 1 }, new double[] { 59.9 }, new double[] { 10.7 }, new string?[] { "Oslo" });

        var (columns, rows) = await ReadAllRowsAsync(EdgeSource(path));
        var row = Assert.Single(rows);

        Assert.Contains("addr_geo_lat", columns);
        Assert.Contains("addr_geo_lon", columns);
        Assert.Contains("addr_city", columns);
        Assert.Equal(59.9, CellOf(columns, row, "addr_geo_lat"));
        Assert.Equal("Oslo", CellOf(columns, row, "addr_city"));
    }

    // ---- JSON shaping of nested columns: lists of bool/double/decimal, escaping, maps ----------------------

    [Fact]
    public async Task BooleanList_BecomesAJsonBooleanArray()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("flags", new DataField<bool>("element")));
        var path = PathFor("boollist.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new bool[] { true, false, true }, new[] { 0, 1, 1 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("[true,false,true]", CellOf(cols, Assert.Single(rows), "flags"));
    }

    [Fact]
    public async Task DoubleList_WithNonFiniteValue_WritesNullForThatElement()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<double>("element")));
        var path = PathFor("dbllist.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new[] { 1.5, double.NaN, 2.25 }, new[] { 0, 1, 1 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("[1.5,null,2.25]", CellOf(cols, Assert.Single(rows), "vals"));
    }

    [Fact]
    public async Task DecimalList_BecomesAJsonNumberArray()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("amounts", new DecimalDataField("element", 18, 2)));
        var path = PathFor("declist.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new decimal[] { 1.50m, 2.25m }, new[] { 0, 1 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("[1.50,2.25]", CellOf(cols, Assert.Single(rows), "amounts"));
    }

    [Fact]
    public async Task StringList_EscapesJsonSpecialCharactersAndNonAscii()
    {
        // System.Text.Json's default encoder escapes the quote as ", the backslash as \\, the markup
        // characters < > & as < > &, control characters as \t, and non-ASCII as \uXXXX.
        var quote = ((char)34).ToString(CultureInfo.InvariantCulture);
        var backslash = ((char)92).ToString(CultureInfo.InvariantCulture);
        var tab = ((char)9).ToString(CultureInfo.InvariantCulture);
        var eacute = ((char)0x00E9).ToString(CultureInfo.InvariantCulture);

        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("words", new DataField<string>("element")));
        var path = PathFor("strlist.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new string?[] { "a" + quote + "b", "c" + backslash + "d", "<x>&", "tab" + tab, "caf" + eacute },
                new[] { 0, 1, 1, 1, 1 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal(
            "[\"a\\u0022b\",\"c\\\\d\",\"\\u003Cx\\u003E\\u0026\",\"tab\\t\",\"caf\\u00E9\"]",
            CellOf(cols, Assert.Single(rows), "words"));
    }

    [Fact]
    public async Task Map_WithNullValue_KeepsTheKeyWithAJsonNull()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new MapField("props", new DataField<int>("key"), new DataField<string>("value")));
        var path = PathFor("mapnullval.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 7 }, new[] { 0 }),
            (new string?[] { null }, new[] { 0 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("{\"7\":null}", CellOf(cols, Assert.Single(rows), "props"));
    }

    [Fact]
    public async Task Map_WithDuplicateKeys_EmitsEveryEntryFaithfully()
    {
        // Parquet maps are not guaranteed unique; the reader serializes each entry it reads, so a duplicate key
        // appears twice rather than being silently dropped or throwing.
        var schema = new ParquetSchema(new DataField<int>("id"), new MapField("props", new DataField<int>("key"), new DataField<string>("value")));
        var path = PathFor("mapdup.parquet");
        await WriteRepAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 7, 7 }, new[] { 0, 1 }),
            (new string?[] { "a", "b" }, new[] { 0, 1 }));

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));
        Assert.Equal("{\"7\":\"a\",\"7\":\"b\"}", CellOf(cols, Assert.Single(rows), "props"));
    }

    // ---- Nested column across multiple row groups ----------------------------------------------------------

    [Fact]
    public async Task ScalarList_AcrossTwoRowGroups_ReassemblesEachGroupAndKeepsRowOrder()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<int>("element")));
        var path = PathFor("listgroups.parquet");
        var leaves = schema.GetDataFields();
        await using (var stream = File.Create(path))
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            using (var rg = writer.CreateRowGroup())
            {
                await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 1, 2 }));
                await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 10, 20, 30 }, repetitionLevels: new[] { 0, 1, 0 }));
            }

            using (var rg = writer.CreateRowGroup())
            {
                await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 3 }));
                await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 40 }, repetitionLevels: new[] { 0 }));
            }
        }

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(path));

        Assert.Equal(3, rows.Count);
        Assert.Equal("[10,20]", CellOf(cols, rows[0], "vals"));
        Assert.Equal("[30]", CellOf(cols, rows[1], "vals"));
        Assert.Equal("[40]", CellOf(cols, rows[2], "vals"));
        Assert.Equal(new object?[] { 1L, 2L, 3L }, rows.Select(r => CellOf(cols, r, "RowNumber_DW")).ToArray());
    }

    // ---- Cross-file schema-evolution boundaries -----------------------------------------------------------

    [Fact]
    public async Task SchemaEvolution_IntVersusLong_WidensToStringAndRendersBothFaithfully()
    {
        await WriteOneGroupAsync(PathFor("a.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DataField<int>("v")),
            new int[] { 1 }, new int[] { 100 });
        await WriteOneGroupAsync(PathFor("b.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DataField<long>("v")),
            new int[] { 2 }, new long[] { 9_000_000_000L });

        var options = new Dictionary<string, string?> { ["srcFile"] = "*.parquet" };
        var columns = await _reader.GetColumnsAsync(EdgeSource(_dir, options));
        Assert.Equal("nvarchar(max)", columns.Single(c => c.Name == "v").SqlType);   // int vs long disagree -> widened

        var (cols, rows) = await ReadAllRowsAsync(EdgeSource(_dir, options));
        Assert.Equal("100", CellOf(cols, rows.Single(r => Equals(CellOf(cols, r, "id"), 1)), "v"));
        Assert.Equal("9000000000", CellOf(cols, rows.Single(r => Equals(CellOf(cols, r, "id"), 2)), "v"));
    }

    [Fact]
    public async Task SchemaEvolution_DecimalScaleDisagreement_WidensToString()
    {
        await WriteOneGroupAsync(PathFor("d2.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DecimalDataField("amount", 18, 2)),
            new int[] { 1 }, new decimal[] { 1.50m });
        await WriteOneGroupAsync(PathFor("d4.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DecimalDataField("amount", 18, 4)),
            new int[] { 2 }, new decimal[] { 2.3456m });

        var columns = await _reader.GetColumnsAsync(
            EdgeSource(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));

        // Same CLR decimal but different SQL type (scale 2 vs 4); the lossless union widens to nvarchar(max).
        Assert.Equal("nvarchar(max)", columns.Single(c => c.Name == "amount").SqlType);
    }

    [Fact]
    public async Task SchemaEvolution_IdenticalDecimalAcrossFiles_StaysTypedDecimal()
    {
        await WriteOneGroupAsync(PathFor("e1.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DecimalDataField("amount", 18, 2)),
            new int[] { 1 }, new decimal[] { 1.50m });
        await WriteOneGroupAsync(PathFor("e2.parquet"),
            new ParquetSchema(new DataField<int>("id"), new DecimalDataField("amount", 18, 2)),
            new int[] { 2 }, new decimal[] { 2.75m });

        var columns = await _reader.GetColumnsAsync(
            EdgeSource(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));
        var amount = columns.Single(c => c.Name == "amount");

        Assert.Equal(typeof(decimal), amount.Type);
        Assert.Equal("decimal(18,2)", amount.SqlType);
    }

    // ---- Error paths -------------------------------------------------------------------------------------

    [Fact]
    public async Task MissingLocation_FailsWithALocationError()
    {
        var source = new SourceSpec { Type = "parquet", Location = string.Empty, Options = new Dictionary<string, string?>() };

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("location", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoFilesMatchTheGlob_FailsWithNoSourceFiles()
    {
        // The directory exists but holds no .parquet matching the pattern.
        var source = EdgeSource(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.nosuchext" });

        await Assert.ThrowsAsync<SqlFlow.Core.NoSourceFilesException>(() => _reader.GetColumnsAsync(source));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
