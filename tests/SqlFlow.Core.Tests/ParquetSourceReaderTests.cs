using System.Globalization;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Reader-level tests for <see cref="ParquetSourceReader"/> over real temp .parquet files (no database).
/// Parquet is self-describing, so the reader maps each Parquet type to a SQL type and streams the TYPED value:
/// these tests assert the mapped SQL types, the typed cell values, nulls, NaN/Infinity handling, provenance
/// columns, schema evolution (incl. cross-file type widening), multiple row groups, the expected-column-count
/// check, struct (dotted) columns, lossless name de-collision, and rejection of repeated (list) columns.
/// </summary>
public sealed class ParquetSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_prq_" + Guid.NewGuid().ToString("N"));
    private readonly ParquetSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public ParquetSourceReaderTests() => Directory.CreateDirectory(_dir);

    private string Path_(string name) => Path.Combine(_dir, name);

    private static SourceSpec Source(string locationFileOrDir, Dictionary<string, string?>? options = null)
        => new() { Type = "parquet", Location = locationFileOrDir, Options = options ?? new Dictionary<string, string?>() };

    private static async Task WriteAsync(string path, ParquetSchema schema, params Array[][] rowGroups)
    {
        var leaves = schema.GetDataFields();
        await using var stream = File.Create(path);
        using var writer = await ParquetWriter.CreateAsync(schema, stream);
        foreach (var rowGroupColumns in rowGroups)
        {
            using var rg = writer.CreateRowGroup();
            for (var i = 0; i < leaves.Length; i++)
            {
                await rg.WriteColumnAsync(new DataColumn(leaves[i], rowGroupColumns[i]));
            }
        }
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadAllAsync(SourceSpec source)
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

    private static object? Cell(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    [Fact]
    public async Task ReadsColumnsAndTypedRows()
    {
        var id = new DataField<int>("id");
        var name = new DataField<string>("name");
        var path = Path_("basic.parquet");
        await WriteAsync(path, new ParquetSchema(id, name),
            [new int[] { 1, 2, 3 }, new string?[] { "Ann", "Bob", "Cy" }]);

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Equal(new[] { "id", "name" }, columns.Take(2).ToArray());
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, Cell(columns, rows[0], "id"));        // typed int, not "1"
        Assert.Equal("Bob", Cell(columns, rows[1], "name"));
        Assert.Contains("FileName_DW", columns);
        Assert.Equal(1L, Cell(columns, rows[0], "RowNumber_DW"));
        Assert.Equal(3L, Cell(columns, rows[2], "RowNumber_DW"));
    }

    [Fact]
    public async Task MapsNativeTypesToSqlTypes_AndStreamsTypedValues()
    {
        var i32 = new DataField<int>("i32");
        var i64 = new DataField<long>("i64");
        var dbl = new DataField<double>("dbl");
        var flag = new DataField<bool>("flag");
        var price = new DecimalDataField("price", 18, 2);
        var ts = new DataField<DateTime>("ts");
        var path = Path_("types.parquet");

        await WriteAsync(path, new ParquetSchema(i32, i64, dbl, flag, price, ts),
        [
            new int[] { 42 },
            new long[] { 9_000_000_000L },
            new double[] { 3.5 },
            new bool[] { true },
            new decimal[] { 9.99m },
            new[] { new DateTime(2024, 1, 15, 13, 45, 30, DateTimeKind.Utc) },
        ]);

        var columns = await _reader.GetColumnsAsync(Source(path));
        var sql = columns.ToDictionary(c => c.Name, c => c.SqlType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("int", sql["i32"]);
        Assert.Equal("bigint", sql["i64"]);
        Assert.Equal("float", sql["dbl"]);
        Assert.Equal("bit", sql["flag"]);
        Assert.Equal("decimal(18,2)", sql["price"]);
        Assert.Equal("datetime2", sql["ts"]);

        var (cols, rows) = await ReadAllAsync(Source(path));
        var row = Assert.Single(rows);
        Assert.Equal(42, Cell(cols, row, "i32"));
        Assert.Equal(9_000_000_000L, Cell(cols, row, "i64"));
        Assert.Equal(3.5, Cell(cols, row, "dbl"));
        Assert.Equal(true, Cell(cols, row, "flag"));
        Assert.Equal(9.99m, Cell(cols, row, "price"));
        var tsValue = (DateTime)Cell(cols, row, "ts")!;   // timestamp round-trips as a typed DateTime
        Assert.Equal(new DateOnly(2024, 1, 15), DateOnly.FromDateTime(tsValue));
    }

    [Fact]
    public async Task FloatSpecials_NaNAndInfinity_BecomeNull()
    {
        var v = new DataField<double>("v");
        var path = Path_("specials.parquet");
        await WriteAsync(path, new ParquetSchema(v),
            [new[] { 1.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity }]);

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Equal(1.5, Cell(columns, rows[0], "v"));
        Assert.Null(Cell(columns, rows[1], "v"));   // NaN -> NULL (SQL float cannot represent it)
        Assert.Null(Cell(columns, rows[2], "v"));   // +Infinity -> NULL
        Assert.Null(Cell(columns, rows[3], "v"));   // -Infinity -> NULL
    }

    [Fact]
    public async Task NullableColumn_YieldsNullCells()
    {
        var id = new DataField<int>("id");
        var note = new DataField<string>("note");
        var score = new DataField<int?>("score");
        var path = Path_("nulls.parquet");
        await WriteAsync(path, new ParquetSchema(id, note, score),
            [new int[] { 1, 2 }, new string?[] { "x", null }, new int?[] { 7, null }]);

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Equal("x", Cell(columns, rows[0], "note"));
        Assert.Null(Cell(columns, rows[1], "note"));
        Assert.Equal(7, Cell(columns, rows[0], "score"));
        Assert.Null(Cell(columns, rows[1], "score"));
    }

    [Fact]
    public async Task MultipleRowGroups_AllRowsInOrder()
    {
        var id = new DataField<int>("id");
        var path = Path_("groups.parquet");
        await WriteAsync(path, new ParquetSchema(id),
            [new int[] { 1, 2 }],
            [new int[] { 3, 4 }]);

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Equal(4, rows.Count);
        Assert.Equal(new object?[] { 1, 2, 3, 4 }, rows.Select(r => Cell(columns, r, "id")).ToArray());
        Assert.Equal(new long?[] { 1L, 2L, 3L, 4L }, rows.Select(r => (long?)Cell(columns, r, "RowNumber_DW")).ToArray());
    }

    [Fact]
    public async Task SystemColumns_AreInjectedWithCorrectTypes()
    {
        var path = Path_("sys.parquet");
        await WriteAsync(path, new ParquetSchema(new DataField<int>("id")), [new int[] { 1 }]);

        var columns = await _reader.GetColumnsAsync(Source(path));
        var byName = columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(typeof(int), byName["id"]);   // source column now carries its real type
        // File provenance lands as strings (the raw layer is untyped; the transformation view applies
        // the real types), while the row number stays a real long from the reader.
        Assert.Equal(typeof(string), byName["FileName_DW"]);
        Assert.Equal(typeof(string), byName["FileDate_DW"]);
        Assert.Equal(typeof(string), byName["FileSize_DW"]);
        Assert.Equal(typeof(long), byName["RowNumber_DW"]);
    }

    [Fact]
    public async Task SchemaEvolution_UnionsColumnsAcrossFiles()
    {
        await WriteAsync(Path_("v1.parquet"), new ParquetSchema(new DataField<int>("id"), new DataField<string>("name")),
            [new int[] { 1 }, new string?[] { "Ann" }]);
        await WriteAsync(Path_("v2.parquet"), new ParquetSchema(new DataField<int>("id"), new DataField<string>("email")),
            [new int[] { 2 }, new string?[] { "b@x.io" }]);

        var (columns, rows) = await ReadAllAsync(Source(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));

        Assert.Contains("name", columns);
        Assert.Contains("email", columns);
        Assert.Equal(2, rows.Count);
        var v1 = rows.Single(r => Equals(Cell(columns, r, "id"), 1));
        Assert.Equal("Ann", Cell(columns, v1, "name"));
        Assert.Null(Cell(columns, v1, "email"));
    }

    [Fact]
    public async Task SchemaEvolution_TypeConflict_WidensToString()
    {
        await WriteAsync(Path_("a.parquet"), new ParquetSchema(new DataField<int>("id"), new DataField<int>("v")),
            [new int[] { 1 }, new int[] { 100 }]);
        await WriteAsync(Path_("b.parquet"), new ParquetSchema(new DataField<int>("id"), new DataField<string>("v")),
            [new int[] { 2 }, new string?[] { "hello" }]);

        var columns = await _reader.GetColumnsAsync(Source(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));
        Assert.Equal(typeof(int), columns.Single(c => c.Name == "id").Type);      // agrees across files
        Assert.Equal(typeof(string), columns.Single(c => c.Name == "v").Type);    // int vs string -> widened

        var (cols, rows) = await ReadAllAsync(Source(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));
        Assert.Equal("100", Cell(cols, rows.Single(r => Equals(Cell(cols, r, "id"), 1)), "v"));   // int rendered as string
        Assert.Equal("hello", Cell(cols, rows.Single(r => Equals(Cell(cols, r, "id"), 2)), "v"));
    }

    [Fact]
    public async Task SchemaEvolution_SameSqlTypeFromDifferentParquetTypes_StaysTyped()
    {
        // A Parquet ulong and a decimal(20,0) both map to SQL decimal(20,0): the union must keep the typed
        // column, not widen to nvarchar just because the producers differ on redundant precision/scale metadata.
        await WriteAsync(Path_("u.parquet"), new ParquetSchema(new DataField<int>("id"), new DataField<ulong>("amount")),
            [new int[] { 1 }, new ulong[] { 100UL }]);
        await WriteAsync(Path_("d.parquet"), new ParquetSchema(new DataField<int>("id"), new DecimalDataField("amount", 20, 0)),
            [new int[] { 2 }, new decimal[] { 200m }]);

        var columns = await _reader.GetColumnsAsync(Source(_dir, new Dictionary<string, string?> { ["srcFile"] = "*.parquet" }));
        var amount = columns.Single(c => c.Name == "amount");

        Assert.Equal(typeof(decimal), amount.Type);
        Assert.Equal("decimal(20,0)", amount.SqlType);   // not widened to nvarchar(max)
    }

    [Fact]
    public async Task StructLeafCollidingWithFlatColumn_IsDeCollidedLossless()
    {
        var city = new DataField<string>("city");
        var flat = new DataField<string>("address_city");
        var schema = new ParquetSchema(new StructField("address", city), flat);
        var path = Path_("collide.parquet");
        await WriteAsync(path, schema, [new string?[] { "Oslo" }, new string?[] { "FLAT" }]);

        var (columns, rows) = await ReadAllAsync(Source(path));
        var row = Assert.Single(rows);

        Assert.Contains("address_city", columns);
        Assert.Contains("address_city_2", columns);
        var values = new[] { Cell(columns, row, "address_city"), Cell(columns, row, "address_city_2") };
        Assert.Contains("Oslo", values);
        Assert.Contains("FLAT", values);
    }

    [Fact]
    public async Task CaseOnlyDifferingColumns_AreDeCollidedLossless()
    {
        var schema = new ParquetSchema(new DataField<string>("Name"), new DataField<string>("name"));
        var path = Path_("case.parquet");
        await WriteAsync(path, schema, [new string?[] { "UPPER" }, new string?[] { "lower" }]);

        var (columns, rows) = await ReadAllAsync(Source(path));
        var row = Assert.Single(rows);

        Assert.Equal(2, columns.Count(c => c.StartsWith("Name", StringComparison.OrdinalIgnoreCase) || c.StartsWith("name", StringComparison.OrdinalIgnoreCase)));
        var values = columns.Where(c => c.StartsWith("name", StringComparison.OrdinalIgnoreCase)).Select(c => Cell(columns, row, c)).ToList();
        Assert.Contains("UPPER", values);
        Assert.Contains("lower", values);
    }

    [Fact]
    public async Task StructColumns_BecomeDottedNames()
    {
        var city = new DataField<string>("city");
        var zip = new DataField<string>("zip");
        var schema = new ParquetSchema(new DataField<int>("id"), new StructField("address", city, zip));
        var path = Path_("struct.parquet");
        await WriteAsync(path, schema, [new int[] { 1 }, new string?[] { "Oslo" }, new string?[] { "0001" }]);

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Contains("address_city", columns);
        Assert.Contains("address_zip", columns);
        Assert.Equal("Oslo", Cell(columns, Assert.Single(rows), "address_city"));
    }

    [Fact]
    public async Task ExpectedColumnCountMismatch_FailsClearly()
    {
        var path = Path_("count.parquet");
        await WriteAsync(path, new ParquetSchema(new DataField<int>("id"), new DataField<string>("name")),
            [new int[] { 1 }, new string?[] { "x" }]);

        var source = Source(path, new Dictionary<string, string?> { ["expectedColumnCount"] = "5" });
        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("ExpectedColumnCount", ex.Message);
    }

    [Fact]
    public async Task TimeSpanColumn_MapsToStringAndHoldsDurationsBeyond24h()
    {
        var dur = new DataField<TimeSpan>("dur");
        var path = Path_("dur.parquet");
        await WriteAsync(path, new ParquetSchema(dur),
            [new[] { new TimeSpan(13, 45, 30), TimeSpan.FromHours(25), new TimeSpan(-1, -2, 0, 0) }]);

        // TimeSpan is NOT mapped to SQL 'time' (which only holds 0..24h) - a duration would overflow the load.
        var columns = await _reader.GetColumnsAsync(Source(path));
        Assert.Equal("nvarchar(max)", columns.Single(c => c.Name == "dur").SqlType);

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal("13:45:30", Cell(cols, rows[0], "dur"));
        Assert.Equal("1.01:00:00", Cell(cols, rows[1], "dur"));   // 25h survives losslessly as a string
        Assert.NotNull(Cell(cols, rows[2], "dur"));               // negative duration survives too
    }

    [Fact]
    public async Task DecimalPrecisionAbove38_IsClampedToSqlMax()
    {
        var big = new DecimalDataField("big", 40, 5);
        var path = Path_("bigdec.parquet");
        await WriteAsync(path, new ParquetSchema(big), [new decimal[] { 12345.67m }]);

        var columns = await _reader.GetColumnsAsync(Source(path));
        Assert.Equal("decimal(38,5)", columns.Single(c => c.Name == "big").SqlType);   // clamped from 40

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal(12345.67m, Cell(cols, Assert.Single(rows), "big"));
    }

    private static async Task WriteRawAsync(string path, ParquetSchema schema, params (Array Data, int[]? Rep)[] columns)
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

    [Fact]
    public async Task ListOfScalars_BecomesJsonArrayColumn()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<int>("element")));
        var path = Path_("list.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 10, 20 }, null),                 // id: 2 rows
            (new int[] { 1, 2, 3 }, new[] { 0, 1, 0 }));  // vals: row0=[1,2], row1=[3]

        var columns = await _reader.GetColumnsAsync(Source(path));
        Assert.Equal("nvarchar(max)", columns.Single(c => c.Name == "vals").SqlType);

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal(2, rows.Count);
        Assert.Equal("[1,2]", Cell(cols, rows[0], "vals"));
        Assert.Equal("[3]", Cell(cols, rows[1], "vals"));
    }

    [Fact]
    public async Task NullableList_MixingEmptyNullAndElements_DoesNotShiftValues()
    {
        // Regression (critical): a NULLABLE element leaf is read slot-aligned. row0=[10,20], row1=[], row2=null,
        // row3=[null,30], row4=[40]. A dense cursor would lose 30 and 40 (read the empty/null placeholders).
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<int?>("element")));
        var path = Path_("nullablelists.parquet");
        var leaves = schema.GetDataFields();
        await using (var stream = File.Create(path))
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        using (var rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 1, 2, 3, 4, 5 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 10, 20, 30, 40 },
                repetitionLevels: new[] { 0, 1, 0, 0, 0, 1, 0 }, definitionLevels: new[] { 3, 3, 1, 0, 2, 3, 3 }));
        }

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal(5, rows.Count);
        Assert.Equal("[10,20]", Cell(cols, rows[0], "vals"));
        Assert.Equal("[]", Cell(cols, rows[1], "vals"));
        Assert.Null(Cell(cols, rows[2], "vals"));
        Assert.Equal("[null,30]", Cell(cols, rows[3], "vals"));
        Assert.Equal("[40]", Cell(cols, rows[4], "vals"));
    }

    [Fact]
    public async Task EmptyListBeforeElements_DoesNotShiftValues()
    {
        // Regression (critical): an empty list consumes no dense Data entry. row0=[], row1=[5,6]. A plain slot
        // index would read 6 then run off the end; the cursor must advance only for existing elements.
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<int>("element")));
        var path = Path_("emptylist.parquet");
        var leaves = schema.GetDataFields();
        await using (var stream = File.Create(path))
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        using (var rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 10, 20 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 5, 6 },
                repetitionLevels: new[] { 0, 0, 1 }, definitionLevels: new[] { 1, 2, 2 }));
        }

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal(2, rows.Count);
        Assert.Equal("[]", Cell(cols, rows[0], "vals"));
        Assert.Equal("[5,6]", Cell(cols, rows[1], "vals"));
    }

    [Fact]
    public async Task ListWithNullElement_PreservesNullInJson()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("vals", new DataField<int?>("element")));
        var path = Path_("listnull.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int?[] { 10, null, 30 }, new[] { 0, 1, 1 }));   // vals = [10, null, 30]

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal("[10,null,30]", Cell(cols, Assert.Single(rows), "vals"));
    }

    [Fact]
    public async Task Map_BecomesJsonObjectColumn()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new MapField("props", new DataField<int>("key"), new DataField<string>("value")));
        var path = Path_("map.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 1, 2 }, new[] { 0, 1 }),                 // keys
            (new string?[] { "a", "b" }, new[] { 0, 1 }));        // values

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal("nvarchar(max)", (await _reader.GetColumnsAsync(Source(path))).Single(c => c.Name == "props").SqlType);
        Assert.Equal("{\"1\":\"a\",\"2\":\"b\"}", Cell(cols, Assert.Single(rows), "props"));
    }

    [Fact]
    public async Task ListOfStruct_BecomesJsonArrayOfObjects()
    {
        var element = new StructField("element", new DataField<int>("a"), new DataField<string>("b"));
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("items", element));
        var path = Path_("los.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 10, 20 }, new[] { 0, 1 }),              // items[*].a
            (new string?[] { "x", "y" }, new[] { 0, 1 }));       // items[*].b

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal("[{\"a\":10,\"b\":\"x\"},{\"a\":20,\"b\":\"y\"}]", Cell(cols, Assert.Single(rows), "items"));
    }

    [Fact]
    public async Task ListOfStruct_WithFieldNamedElement_StaysObjectArray()
    {
        // Regression: a single-field struct whose field is literally named "element" must not be mistaken for a
        // scalar list. Shape is now decided from the schema structure, not the leaf-path name.
        var element = new StructField("element", new DataField<int>("element"));
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("rows", element));
        var path = Path_("losele.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 10, 20 }, new[] { 0, 1 }));   // rows = [{element:10},{element:20}]

        var (cols, rows) = await ReadAllAsync(Source(path));
        Assert.Equal("[{\"element\":10},{\"element\":20}]", Cell(cols, Assert.Single(rows), "rows"));
    }

    [Fact]
    public async Task MapWithStructValue_IsRejectedClearly()
    {
        // Regression: a map<k,struct> has 3 leaves; the old 2-leaf assumption dropped fields. Reject it clearly.
        var value = new StructField("value", new DataField<int>("x"), new DataField<int>("y"));
        var schema = new ParquetSchema(new DataField<int>("id"), new MapField("m", new DataField<int>("key"), value));
        var path = Path_("mapstruct.parquet");
        var leaves = schema.GetDataFields();
        await using (var stream = File.Create(path))
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        using (var rg = writer.CreateRowGroup())
        {
            await rg.WriteColumnAsync(new DataColumn(leaves[0], new int[] { 1 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[1], new int[] { 7 }, repetitionLevels: new[] { 0 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[2], new int[] { 1 }, repetitionLevels: new[] { 0 }));
            await rg.WriteColumnAsync(new DataColumn(leaves[3], new int[] { 2 }, repetitionLevels: new[] { 0 }));
        }

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(Source(path)));
        Assert.Contains("non-scalar", ex.Message);
    }

    [Fact]
    public async Task ExpectedColumnCount_CountsLeafColumns_NotOutputColumns()
    {
        var schema = new ParquetSchema(new DataField<int>("id"), new MapField("attrs", new DataField<int>("key"), new DataField<string>("value")));
        var path = Path_("count2.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 1 }, new[] { 0 }),
            (new string?[] { "a" }, new[] { 0 }));

        // 3 leaf columns (id, attrs/key_value/key, attrs/key_value/value) collapse to 2 output columns.
        await _reader.GetColumnsAsync(Source(path, new Dictionary<string, string?> { ["expectedColumnCount"] = "3" }));
        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(
            () => _reader.GetColumnsAsync(Source(path, new Dictionary<string, string?> { ["expectedColumnCount"] = "2" })));
        Assert.Contains("leaf column", ex.Message);
    }

    [Fact]
    public async Task DeeplyNestedListOfList_IsRejectedClearly()
    {
        // A single-level list/map/struct is reassembled to JSON, but list-of-list (repetition >= 2) is not.
        var inner = new ListField("element", new DataField<int>("element"));
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("matrix", inner));
        var path = Path_("matrix.parquet");
        await WriteRawAsync(path, schema,
            (new int[] { 1 }, null),
            (new int[] { 1, 2 }, new[] { 0, 2 }));   // matrix = [[1,2]]

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(Source(path)));
        Assert.Contains("does not flatten", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
