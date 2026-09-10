using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>The DuckDB relation/option builder: format inference and selection, injection-safe path literals,
/// verbatim-query wrapping, partitioning flags, and the projection/filter/extensions/init options.</summary>
public sealed class DuckDbQueryTests
{
    private static SourceSpec Source(string? location, params (string Key, string? Value)[] options)
        => new()
        {
            Type = "duckdb",
            Location = location,
            Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void Parquet_IsTheDefaultScan()
        => Assert.Equal("read_parquet('/data/x.parquet')", DuckDbQuery.BuildRelation(Source("/data/x.parquet")));

    [Fact]
    public void Csv_InferredFromExtension()
        => Assert.Equal("read_csv_auto('/data/x.csv')", DuckDbQuery.BuildRelation(Source("/data/x.csv")));

    [Fact]
    public void Format_OptionOverridesInference()
        => Assert.Equal("delta_scan('/lake/dim')", DuckDbQuery.BuildRelation(Source("/lake/dim", ("format", "delta"))));

    [Fact]
    public void Json_Scan()
        => Assert.Equal("read_json_auto('/data/x.json')", DuckDbQuery.BuildRelation(Source("/data/x.json")));

    [Fact]
    public void VerbatimQuery_IsWrappedAsSubquery()
        => Assert.Equal("(SELECT 1 AS a) AS _src", DuckDbQuery.BuildRelation(Source(null, ("query", "SELECT 1 AS a"))));

    [Fact]
    public void Location_SingleQuotesAreEscaped()
        => Assert.Equal("read_parquet('/data/o''brien.parquet')", DuckDbQuery.BuildRelation(Source("/data/o'brien.parquet")));

    [Fact]
    public void HivePartitioning_AddsArg()
        => Assert.Equal("read_parquet('/data/**/*.parquet', hive_partitioning = true)",
            DuckDbQuery.BuildRelation(Source("/data/**/*.parquet", ("hivePartitioning", "true"))));

    [Fact]
    public void MissingLocationAndQuery_Throws()
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.BuildRelation(Source(null)));

    [Fact]
    public void Options_AreParsed()
    {
        var source = Source("/d/x.parquet",
            ("columns", "a, b, c"), ("filter", "a > 1"), ("extensions", "azure, httpfs"),
            ("init", "SET s3_region='eu'; CREATE SECRET x (TYPE AZURE)"));

        Assert.Equal(["a", "b", "c"], DuckDbQuery.Columns(source));
        Assert.Equal("a > 1", DuckDbQuery.Filter(source));
        Assert.Equal(["azure", "httpfs"], DuckDbQuery.Extensions(source));
        Assert.Equal(["SET s3_region='eu'", "CREATE SECRET x (TYPE AZURE)"], DuckDbQuery.InitStatements(source));
    }

    [Fact]
    public void Extensions_RejectUnsafeNames()
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.Extensions(Source("/d/x.parquet", ("extensions", "azure; DROP"))));

    [Fact]
    public void DeltaType_ScansThroughDeltaScan()
    {
        var source = new SourceSpec { Type = "delta", Location = "/lake/dim_customer", Options = new Dictionary<string, string?>() };
        Assert.Equal("delta", DuckDbQuery.EffectiveFormat(source));
        Assert.Equal("delta_scan('/lake/dim_customer')", DuckDbQuery.BuildRelation(source));
    }

    [Fact]
    public void Delta_TimeTravelByVersion()
        => Assert.Equal("delta_scan('/lake/t', version = 7)",
            DuckDbQuery.BuildRelation(Source("/lake/t", ("format", "delta"), ("deltaVersion", "7"))));

    [Fact]
    public void Delta_TimeTravelByTimestamp()
        => Assert.Equal("delta_scan('/lake/t', timestamp = '2026-01-01 00:00:00')",
            DuckDbQuery.BuildRelation(Source("/lake/t", ("format", "delta"), ("deltaTimestamp", "2026-01-01 00:00:00"))));

    [Fact]
    public void Delta_NegativeVersionRejected()
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.BuildRelation(Source("/lake/t", ("format", "delta"), ("deltaVersion", "-1"))));

    [Fact]
    public void RequiredExtensions_AutoLoadsDeltaAndCloud()
    {
        // A Delta table on ADLS needs both the delta and azure extensions, with no manual wiring.
        var adls = new SourceSpec { Type = "delta", Location = "abfss://data@acct.dfs.core.windows.net/dim", Options = new Dictionary<string, string?>() };
        Assert.Contains("delta", DuckDbQuery.RequiredExtensions(adls));
        Assert.Contains("azure", DuckDbQuery.RequiredExtensions(adls));

        // Parquet on S3 needs httpfs; a local parquet needs nothing.
        Assert.Contains("httpfs", DuckDbQuery.RequiredExtensions(Source("s3://bucket/x.parquet")));
        Assert.Empty(DuckDbQuery.RequiredExtensions(Source("/local/x.parquet")));
    }

    [Fact]
    public void RequiredExtensions_MergesAndDedupesUserExtensions()
    {
        var source = new SourceSpec { Type = "delta", Location = "abfss://c@a.dfs.core.windows.net/t", Options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["extensions"] = "delta, spatial" } };
        var ext = DuckDbQuery.RequiredExtensions(source);
        Assert.Equal(1, ext.Count(e => e == "delta")); // not duplicated
        Assert.Contains("spatial", ext);
        Assert.Contains("azure", ext);
    }
}
