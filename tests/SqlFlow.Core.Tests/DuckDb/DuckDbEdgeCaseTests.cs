using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// Non-overlapping edge cases for the DuckDB feature area: format inference/selection boundaries, the
/// hive/union partitioning flags' strict truthiness, Delta time-travel argument precedence, required-extension
/// ordering across format and cloud scheme, predicate composition and watermark literal rendering, type-map
/// boundaries (decimal defaults, scale clamping, varchar length ceiling, unsigned widening metadata), and the
/// Azure secret decision/escaping rules. Pure in-memory text generation: no libduckdb, no network, deterministic.
/// </summary>
public sealed class DuckDbEdgeCaseTests
{
    private static SourceSpec MakeSource(string? location, params (string Key, string? Value)[] options)
        => new()
        {
            Type = "duckdb",
            Location = location,
            Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        };

    private static SourceSpec MakeTypedSource(string type, string? location, params (string Key, string? Value)[] options)
        => new()
        {
            Type = type,
            Location = location,
            Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        };

    private static AzureStorageCredential MakeCredential(
        CloudAuthMode mode, string account = "acct", string? tenant = null, string? client = null, string? secret = null, bool runningInAzure = false)
        => new() { AccountName = account, Mode = mode, TenantId = tenant, ClientId = client, ClientSecret = secret, RunningInAzure = runningInAzure };

    private sealed class FixedCredentialProvider(AzureStorageCredential? credential) : ICloudCredentialProvider
    {
        public AzureStorageCredential? ResolveAzureStorage(string location) => credential;
    }

    // ---- Format inference and selection boundaries ----

    [Theory]
    [InlineData("/data/x.tsv", "csv")]
    [InlineData("/data/x.txt", "csv")]
    [InlineData("/data/x.ndjson", "json")]
    [InlineData("/data/x.jsonl", "json")]
    [InlineData("/data/x.PARQUET", "parquet")]   // extension match is case-insensitive
    [InlineData("/data/x.CsV", "csv")]
    public void EffectiveFormat_InfersFromExtension_CaseInsensitively(string location, string expected)
        => Assert.Equal(expected, DuckDbQuery.EffectiveFormat(MakeSource(location)));

    [Fact]
    public void EffectiveFormat_ExtensionlessLocation_DefaultsToParquet()
        => Assert.Equal("parquet", DuckDbQuery.EffectiveFormat(MakeSource("/lake/dim_customer")));

    [Fact]
    public void EffectiveFormat_UnknownExtension_DefaultsToParquet()
        => Assert.Equal("parquet", DuckDbQuery.EffectiveFormat(MakeSource("/data/x.orc")));

    [Fact]
    public void EffectiveFormat_NoLocation_DefaultsToParquet()
        => Assert.Equal("parquet", DuckDbQuery.EffectiveFormat(MakeSource(null)));

    [Fact]
    public void EffectiveFormat_ExplicitFormatIsLowercased()
        => Assert.Equal("delta", DuckDbQuery.EffectiveFormat(MakeSource("/lake/t", ("format", "DELTA"))));

    [Fact]
    public void EffectiveFormat_DeltaTypeWins_OverConflictingFormatOption()
    {
        // The source type 'delta' is authoritative even if the format option says otherwise.
        var source = MakeTypedSource("delta", "/lake/t", ("format", "parquet"));
        Assert.Equal("delta", DuckDbQuery.EffectiveFormat(source));
    }

    [Fact]
    public void EffectiveFormat_DeltaTypeIsCaseInsensitive()
        => Assert.Equal("delta", DuckDbQuery.EffectiveFormat(MakeTypedSource("Delta", "/lake/t")));

    [Fact]
    public void BuildRelation_PrqFormatAlias_ReadsThroughReadParquet()
        => Assert.Equal("read_parquet('/data/x.bin')", DuckDbQuery.BuildRelation(MakeSource("/data/x.bin", ("format", "prq"))));

    [Theory]
    [InlineData("ndjson")]
    [InlineData("jsonl")]
    public void BuildRelation_JsonFormatAliases_ReadThroughReadJsonAuto(string format)
        => Assert.Equal("read_json_auto('/data/x.dat')", DuckDbQuery.BuildRelation(MakeSource("/data/x.dat", ("format", format))));

    [Fact]
    public void BuildRelation_UnsupportedExplicitFormat_Throws()
    {
        // 'tsv' is inferred to csv from an extension, but is not a recognized explicit format token.
        Assert.Throws<SqlFlowException>(() => DuckDbQuery.BuildRelation(MakeSource("/data/x.dat", ("format", "tsv"))));
    }

    [Fact]
    public void BuildRelation_BlankFormatOption_FallsBackToInference()
        => Assert.Equal("read_csv_auto('/data/x.csv')", DuckDbQuery.BuildRelation(MakeSource("/data/x.csv", ("format", "   "))));

    [Fact]
    public void BuildRelation_QueryWinsOverLocation()
    {
        // When both a query and a location are present, the verbatim query is used and the location is ignored.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/data/ignored.parquet", ("query", "SELECT 9 AS n")));
        Assert.Equal("(SELECT 9 AS n) AS _src", relation);
    }

    [Fact]
    public void BuildRelation_GlobLocation_IsEscapedAndScanned()
        => Assert.Equal("read_parquet('/data/year=*/*.parquet')", DuckDbQuery.BuildRelation(MakeSource("/data/year=*/*.parquet")));

    // ---- Parquet partitioning flag truthiness ----

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("TRUE ")]   // trailing space still trims to true elsewhere, but value here is "TRUE " -> trims -> true
    [InlineData("on")]
    [InlineData("")]
    public void BuildRelation_HivePartitioning_NonCanonicalValues(string value)
    {
        // Only the literal token "true" (case-insensitive, trimmed) enables the flag; everything else is off.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/data/x.parquet", ("hivePartitioning", value)));
        var enabled = string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(enabled, relation.Contains("hive_partitioning = true", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildRelation_UnionByName_AddsArg()
        => Assert.Equal("read_parquet('/data/**/*.parquet', union_by_name = true)",
            DuckDbQuery.BuildRelation(MakeSource("/data/**/*.parquet", ("unionByName", "true"))));

    [Fact]
    public void BuildRelation_HiveAndUnion_AppearInDeterministicOrder()
        => Assert.Equal("read_parquet('/data/p', hive_partitioning = true, union_by_name = true)",
            DuckDbQuery.BuildRelation(MakeSource("/data/p", ("hivePartitioning", "true"), ("unionByName", "true"))));

    [Fact]
    public void BuildRelation_PartitionFlags_IgnoredForCsv()
    {
        // The hive/union args are parquet-only; a csv scan never carries them.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/data/x.csv", ("hivePartitioning", "true"), ("unionByName", "true")));
        Assert.Equal("read_csv_auto('/data/x.csv')", relation);
    }

    // ---- Delta time-travel arguments ----

    [Fact]
    public void BuildRelation_DeltaVersionZero_IsValid()
        => Assert.Equal("delta_scan('/lake/t', version = 0)",
            DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("deltaVersion", "0"))));

    [Fact]
    public void BuildRelation_DeltaVersionWinsOverTimestamp()
    {
        // When both are supplied, the version pins the snapshot and the timestamp is not emitted.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("deltaVersion", "3"), ("deltaTimestamp", "2026-01-01 00:00:00")));
        Assert.Equal("delta_scan('/lake/t', version = 3)", relation);
    }

    [Fact]
    public void BuildRelation_DeltaTimestamp_IsSingleQuoteEscaped()
    {
        // A timestamp string carrying an apostrophe cannot break out of the literal.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("deltaTimestamp", "x'y")));
        Assert.Equal("delta_scan('/lake/t', timestamp = 'x''y')", relation);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("9999999999999999999999")]   // overflows long
    public void BuildRelation_DeltaVersion_NonIntegerRejected(string version)
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("deltaVersion", version))));

    [Fact]
    public void BuildRelation_DeltaBlankVersion_FallsThroughToLatest()
    {
        // A blank deltaVersion is treated as absent, so a blank-and-no-timestamp delta scan reads the latest snapshot.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("deltaVersion", "   ")));
        Assert.Equal("delta_scan('/lake/t')", relation);
    }

    [Fact]
    public void BuildRelation_DeltaIgnoresPartitionFlags()
    {
        // hive_partitioning/union_by_name are parquet-reader args; a delta_scan never carries them.
        var relation = DuckDbQuery.BuildRelation(MakeSource("/lake/t", ("format", "delta"), ("hivePartitioning", "true"), ("unionByName", "true")));
        Assert.Equal("delta_scan('/lake/t')", relation);
    }

    // ---- RequiredExtensions ordering and scheme detection ----

    [Fact]
    public void RequiredExtensions_DeltaOnS3_AddsDeltaThenHttpfs()
    {
        var source = MakeTypedSource("delta", "s3://bucket/dim");
        Assert.Equal(["delta", "httpfs"], DuckDbQuery.RequiredExtensions(source).ToArray());
    }

    [Theory]
    [InlineData("az://container/path/x.parquet")]
    [InlineData("abfs://c@acct.dfs.core.windows.net/x.parquet")]
    [InlineData("azure://c/x.parquet")]
    public void RequiredExtensions_AzureSchemes_RequireAzure(string location)
        => Assert.Contains("azure", DuckDbQuery.RequiredExtensions(MakeSource(location)));

    [Theory]
    [InlineData("gs://bucket/x.parquet")]
    [InlineData("gcs://bucket/x.parquet")]
    [InlineData("r2://bucket/x.parquet")]
    [InlineData("http://host/x.parquet")]
    [InlineData("https://host/x.parquet")]
    public void RequiredExtensions_CloudSchemes_RequireHttpfs(string location)
        => Assert.Contains("httpfs", DuckDbQuery.RequiredExtensions(MakeSource(location)));

    [Fact]
    public void RequiredExtensions_AzureAndHttpfs_AreMutuallyExclusive()
    {
        // An abfss location implies azure, not httpfs.
        var ext = DuckDbQuery.RequiredExtensions(MakeSource("abfss://d@acct.dfs.core.windows.net/x.parquet"));
        Assert.Contains("azure", ext);
        Assert.DoesNotContain("httpfs", ext);
    }

    [Fact]
    public void RequiredExtensions_UserExtensionsComeBeforeImplied()
    {
        // User-listed extensions are preserved in order ahead of the format/scheme-implied ones.
        var source = MakeTypedSource("delta", "s3://b/t", ("extensions", "spatial, icu"));
        Assert.Equal(["spatial", "icu", "delta", "httpfs"], DuckDbQuery.RequiredExtensions(source).ToArray());
    }

    [Fact]
    public void RequiredExtensions_LocalParquetWithNoUserExtensions_IsEmpty()
        => Assert.Empty(DuckDbQuery.RequiredExtensions(MakeSource("/local/x.parquet")));

    [Fact]
    public void RequiredExtensions_SchemeMatchIsAnchored_NotSubstring()
    {
        // A path containing "s3" mid-string but not as a scheme must not pull in httpfs.
        Assert.Empty(DuckDbQuery.RequiredExtensions(MakeSource("/data/s3_backup/x.parquet")));
    }

    [Fact]
    public void RequiredExtensions_HttpfsNotDuplicatedWhenUserAlsoListsIt()
    {
        var source = MakeSource("s3://b/x.parquet", ("extensions", "httpfs"));
        var ext = DuckDbQuery.RequiredExtensions(source);
        Assert.Equal(1, ext.Count(e => e == "httpfs"));
    }

    // ---- Extensions / columns / init parsing edge cases ----

    [Fact]
    public void Extensions_LowercasesAndTrims()
        => Assert.Equal(["azure", "httpfs"], DuckDbQuery.Extensions(MakeSource("/d/x.parquet", ("extensions", "  AZURE , HttpFS  "))).ToArray());

    [Fact]
    public void Extensions_EmptyEntriesAreDropped()
        => Assert.Equal(["azure"], DuckDbQuery.Extensions(MakeSource("/d/x.parquet", ("extensions", ",, azure ,"))).ToArray());

    [Theory]
    [InlineData("azure spatial")]   // space is not allowed inside a name
    [InlineData("az-ure")]          // hyphen is not allowed
    [InlineData("ext.name")]        // dot is not allowed
    public void Extensions_RejectNonIdentifierCharacters(string extensions)
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.Extensions(MakeSource("/d/x.parquet", ("extensions", extensions))));

    [Fact]
    public void Extensions_UnderscoreAndDigitsAreValid()
        => Assert.Equal(["ext_2", "v3"], DuckDbQuery.Extensions(MakeSource("/d/x.parquet", ("extensions", "ext_2, v3"))).ToArray());

    [Fact]
    public void Columns_SplitTrimAndDropEmpty()
        => Assert.Equal(["a", "b"], DuckDbQuery.Columns(MakeSource("/d/x.parquet", ("columns", " a , , b "))).ToArray());

    [Fact]
    public void Columns_AbsentOption_IsEmpty()
        => Assert.Empty(DuckDbQuery.Columns(MakeSource("/d/x.parquet")));

    [Fact]
    public void InitStatements_SplitOnSemicolonAndTrim()
        => Assert.Equal(["SET a = 1", "SET b = 2"], DuckDbQuery.InitStatements(MakeSource("/d/x.parquet", ("init", " SET a = 1 ; ; SET b = 2 ; "))).ToArray());

    [Fact]
    public void Filter_BlankOption_IsNull()
        => Assert.Null(DuckDbQuery.Filter(MakeSource("/d/x.parquet", ("filter", "   "))));

    // ---- CombineFilters / IncrementalPredicate composition ----

    [Fact]
    public void CombineFilters_BothBlank_IsNull()
        => Assert.Null(DuckDbQuery.CombineFilters("  ", "\t"));

    [Fact]
    public void CombineFilters_PreservesOrderFirstThenSecond()
        => Assert.Equal("(a = 1) AND (b = 2)", DuckDbQuery.CombineFilters("a = 1", "b = 2"));

    [Fact]
    public void CombineFilters_IsNotRecursivelyParenthesized()
    {
        // Each side is wrapped exactly once; a pre-parenthesized side is not double-wrapped beyond the single layer.
        Assert.Equal("((a = 1)) AND (b = 2)", DuckDbQuery.CombineFilters("(a = 1)", "b = 2"));
    }

    [Fact]
    public void IncrementalPredicate_TextKind_RendersQuotedLiteral()
        => Assert.Equal("\"Region\" > 'eu'",
            DuckDbQuery.IncrementalPredicate(MakeSource("/d/x.parquet",
                (WatermarkPredicate.ColumnOption, "Region"),
                (WatermarkPredicate.ValueOption, "eu"),
                (WatermarkPredicate.KindOption, WatermarkKind.Text.ToString()))));

    [Fact]
    public void IncrementalPredicate_DateTimeKind_RendersTimestampLiteral()
        => Assert.Equal("\"LoadDate\" > TIMESTAMP '2026-01-02 03:04:05.0000000'",
            DuckDbQuery.IncrementalPredicate(MakeSource("/d/x.parquet",
                (WatermarkPredicate.ColumnOption, "LoadDate"),
                (WatermarkPredicate.ValueOption, "2026-01-02T03:04:05.0000000"),
                (WatermarkPredicate.KindOption, WatermarkKind.DateTime.ToString()))));

    [Fact]
    public void IncrementalPredicate_ColumnWithDoubleQuote_IsEscaped()
        => Assert.Equal("\"we\"\"ird\" > 7",
            DuckDbQuery.IncrementalPredicate(MakeSource("/d/x.parquet",
                (WatermarkPredicate.ColumnOption, "we\"ird"),
                (WatermarkPredicate.ValueOption, "7"),
                (WatermarkPredicate.KindOption, WatermarkKind.Whole.ToString()))));

    [Fact]
    public void IncrementalPredicate_BlankColumn_IsNull()
        => Assert.Null(DuckDbQuery.IncrementalPredicate(MakeSource("/d/x.parquet",
            (WatermarkPredicate.ColumnOption, "   "),
            (WatermarkPredicate.ValueOption, "7"),
            (WatermarkPredicate.KindOption, WatermarkKind.Whole.ToString()))));

    [Fact]
    public void CombineFilters_WithIncrementalPredicate_WrapsBothSides()
    {
        var predicate = DuckDbQuery.IncrementalPredicate(MakeSource("/d/x.parquet",
            (WatermarkPredicate.ColumnOption, "Id"),
            (WatermarkPredicate.ValueOption, "100"),
            (WatermarkPredicate.KindOption, WatermarkKind.Whole.ToString())));
        Assert.Equal("(status = 'open') AND (\"Id\" > 100)", DuckDbQuery.CombineFilters("status = 'open'", predicate));
    }

    // ---- Escape ----

    [Fact]
    public void Escape_DoublesEveryQuoteOccurrence()
        => Assert.Equal("a''b''c", DuckDbQuery.Escape("a'b'c"));

    [Fact]
    public void Escape_NoQuotes_IsUnchanged()
        => Assert.Equal("plain", DuckDbQuery.Escape("plain"));

    // ---- Type map boundaries ----

    [Fact]
    public void TypeMap_BareDecimal_UsesDuckDbDefaultPrecisionScale()
    {
        var mapped = DuckDbTypeMap.Map("DECIMAL");
        Assert.Equal("decimal(18,3)", mapped.SqlType);
        Assert.Equal(18, mapped.Precision);
        Assert.Equal(3, mapped.Scale);
    }

    [Fact]
    public void TypeMap_NumericAlias_BehavesLikeDecimal()
        => Assert.Equal("decimal(10,2)", DuckDbTypeMap.Map("NUMERIC(10,2)").SqlType);

    [Fact]
    public void TypeMap_DecimalScaleClampedToPrecision()
    {
        // Scale may never exceed precision; a scale above precision is clamped down to it.
        var mapped = DuckDbTypeMap.Map("DECIMAL(5,10)");
        Assert.Equal("decimal(5,5)", mapped.SqlType);
        Assert.Equal(5, mapped.Scale);
    }

    [Fact]
    public void TypeMap_DecimalPrecisionFloorIsOne()
        => Assert.Equal("decimal(1,0)", DuckDbTypeMap.Map("DECIMAL(0,0)").SqlType);

    [Theory]
    [InlineData("VARCHAR(4000)", "nvarchar(4000)")]
    [InlineData("VARCHAR(4001)", "nvarchar(max)")]
    [InlineData("VARCHAR(1)", "nvarchar(1)")]
    [InlineData("VARCHAR(0)", "nvarchar(max)")]   // a non-positive length collapses to max
    public void TypeMap_VarcharLengthCeiling(string duckType, string expectedSql)
        => Assert.Equal(expectedSql, DuckDbTypeMap.Map(duckType).SqlType);

    [Fact]
    public void TypeMap_IsTrimmedAndCaseInsensitive()
        => Assert.Equal("bigint", DuckDbTypeMap.Map("  bigint  ").SqlType);

    [Theory]
    [InlineData("INT1", "smallint")]
    [InlineData("INT2", "smallint")]
    [InlineData("SHORT", "smallint")]
    [InlineData("INT4", "int")]
    [InlineData("SIGNED", "int")]
    [InlineData("INT8", "bigint")]
    [InlineData("LONG", "bigint")]
    [InlineData("FLOAT4", "real")]
    [InlineData("FLOAT8", "float")]
    [InlineData("BOOL", "bit")]
    [InlineData("LOGICAL", "bit")]
    public void TypeMap_ScalarAliases(string duckType, string expectedSql)
        => Assert.Equal(expectedSql, DuckDbTypeMap.Map(duckType).SqlType);

    [Theory]
    [InlineData("TEXT")]
    [InlineData("STRING")]
    [InlineData("CHAR")]
    [InlineData("BPCHAR")]
    public void TypeMap_UnboundedTextAliases_MapToNvarcharMax(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.Equal("nvarchar(max)", mapped.SqlType);
        Assert.Equal(typeof(string), mapped.ClrType);
    }

    [Theory]
    [InlineData("BYTEA")]
    [InlineData("BINARY")]
    [InlineData("VARBINARY")]
    public void TypeMap_BinaryAliases_MapToVarbinaryMax(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.Equal("varbinary(max)", mapped.SqlType);
        Assert.Equal(typeof(byte[]), mapped.ClrType);
    }

    [Theory]
    [InlineData("TIMESTAMP_S")]
    [InlineData("TIMESTAMP_MS")]
    [InlineData("TIMESTAMP_NS")]
    [InlineData("TIMESTAMP_US")]
    [InlineData("DATETIME")]
    public void TypeMap_TimestampPrecisionVariants_MapToDateTime2(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.Equal("datetime2", mapped.SqlType);
        Assert.Equal(typeof(DateTime), mapped.ClrType);
    }

    [Fact]
    public void TypeMap_TimestampTzAlias_MapsToDateTimeOffset()
    {
        var mapped = DuckDbTypeMap.Map("TIMESTAMPTZ");
        Assert.Equal("datetimeoffset", mapped.SqlType);
        Assert.Equal(typeof(DateTimeOffset), mapped.ClrType);
    }

    [Fact]
    public void TypeMap_Time_MapsToTimeSpan()
    {
        var mapped = DuckDbTypeMap.Map("TIME");
        Assert.Equal("time", mapped.SqlType);
        Assert.Equal(typeof(TimeSpan), mapped.ClrType);
    }

    [Fact]
    public void TypeMap_UnsignedBigInt_CarriesDecimalMetadata()
    {
        // UBIGINT does not fit a signed bigint, so it widens to decimal(20,0) and carries that precision/scale.
        var mapped = DuckDbTypeMap.Map("UBIGINT");
        Assert.Equal("decimal(20,0)", mapped.SqlType);
        Assert.Equal(20, mapped.Precision);
        Assert.Equal(0, mapped.Scale);
        Assert.Equal(typeof(decimal), mapped.ClrType);
    }

    [Theory]
    [InlineData("HUGEINT")]
    [InlineData("UHUGEINT")]
    [InlineData("INT128")]
    public void TypeMap_HugeIntFamily_MapsToDecimal38(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.Equal("decimal(38,0)", mapped.SqlType);
        Assert.Equal(38, mapped.Precision);
    }

    [Fact]
    public void TypeMap_UnsignedWideners_ClrTypesHoldTheRange()
    {
        // USMALLINT widens to int, UINTEGER widens to bigint, so the CLR carrier can hold the unsigned range.
        Assert.Equal(typeof(int), DuckDbTypeMap.Map("USMALLINT").ClrType);
        Assert.Equal(typeof(long), DuckDbTypeMap.Map("UINTEGER").ClrType);
        Assert.Equal(typeof(byte), DuckDbTypeMap.Map("UTINYINT").ClrType);
    }

    [Theory]
    [InlineData("DECIMAL(18,2)[]")]   // a list whose element is decimal is still nested
    [InlineData("UNION(a INTEGER, b VARCHAR)")]
    public void TypeMap_NestedShapes_AreFlaggedAndProjectedAsJson(string duckType)
    {
        var mapped = DuckDbTypeMap.Map(duckType);
        Assert.True(mapped.IsNested);
        Assert.Equal("nvarchar(max)", mapped.SqlType);
    }

    [Fact]
    public void TypeMap_NonNestedScalar_IsNotFlaggedNested()
        => Assert.False(DuckDbTypeMap.Map("INTEGER").IsNested);

    [Theory]
    [InlineData("BIT")]        // bitstring, not boolean: carried as text
    [InlineData("INTERVAL")]
    [InlineData("ENUM('a','b')")]
    public void TypeMap_UnmappedScalars_FallBackToText(string duckType)
        => Assert.Equal("nvarchar(max)", DuckDbTypeMap.Map(duckType).SqlType);

    // ---- Azure secret decision and rendering ----

    [Theory]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData(" OFF ")]
    [InlineData("False")]
    public void AzureSecret_OptOutTokens_SuppressGeneration(string value)
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(
            MakeSource("abfss://d@acct.dfs.core.windows.net/t", ("cloudAuth", value)),
            new FixedCredentialProvider(MakeCredential(CloudAuthMode.DefaultChain))));

    [Theory]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("yes")]
    public void AzureSecret_NonOptOutCloudAuthValues_StillGenerate(string value)
        => Assert.Single(DuckDbAzureSecret.StatementsFor(
            MakeSource("abfss://d@acct.dfs.core.windows.net/t", ("cloudAuth", value)),
            new FixedCredentialProvider(MakeCredential(CloudAuthMode.DefaultChain))));

    [Fact]
    public void AzureSecret_DefaultChain_InAzure_ExactStatement()
        => Assert.Equal(
            "CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'managed_identity;cli;env', ACCOUNT_NAME 'acct')",
            DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.DefaultChain, runningInAzure: true)));

    [Fact]
    public void AzureSecret_ManagedIdentity_RunningInAzureFlagIgnored()
    {
        // An explicit managed-identity mode pins the chain regardless of the running-in-Azure flag.
        var inAzure = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ManagedIdentity, runningInAzure: true));
        var offCloud = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ManagedIdentity, runningInAzure: false));
        Assert.Equal(inAzure, offCloud);
        Assert.Contains("CHAIN 'managed_identity'", inAzure, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureSecret_AzureCli_RunningInAzureFlagIgnored()
    {
        var inAzure = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.AzureCli, runningInAzure: true));
        var offCloud = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.AzureCli, runningInAzure: false));
        Assert.Equal(inAzure, offCloud);
        Assert.Contains("CHAIN 'cli'", inAzure, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureSecret_ServicePrincipal_OmitsChainClause()
    {
        // The service-principal statement uses an explicit provider and never emits a credential chain.
        var sql = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ServicePrincipal, tenant: "t", client: "c", secret: "s"));
        Assert.DoesNotContain("CHAIN", sql, StringComparison.Ordinal);
        Assert.Contains("PROVIDER service_principal", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "cid", "shh")]
    [InlineData("tid", null, "shh")]
    [InlineData("tid", "cid", null)]
    [InlineData("tid", "cid", "  ")]
    public void AzureSecret_ServicePrincipal_MissingAnyMaterial_Throws(string? tenant, string? client, string? secret)
        => Assert.Throws<SqlFlowException>(() =>
            DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ServicePrincipal, tenant: tenant, client: client, secret: secret)));

    [Fact]
    public void AzureSecret_UserAssignedManagedIdentity_Rejected()
        => Assert.Throws<SqlFlowException>(() =>
            DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ManagedIdentity, client: "00000000-0000-0000-0000-000000000001")));

    [Fact]
    public void AzureSecret_ManagedIdentity_BlankClientId_IsSystemAssigned()
    {
        // A whitespace-only client id is treated as no client id (system-assigned), not a user-assigned identity.
        var sql = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.ManagedIdentity, client: "   "));
        Assert.Equal("CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, PROVIDER credential_chain, CHAIN 'managed_identity', ACCOUNT_NAME 'acct')", sql);
    }

    [Fact]
    public void AzureSecret_AccountNameWithQuote_IsEscapedInDefaultChain()
    {
        var sql = DuckDbAzureSecret.CreateStatement(MakeCredential(CloudAuthMode.DefaultChain, account: "ac'me"));
        Assert.Contains("ACCOUNT_NAME 'ac''me'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureSecret_UsesFixedSecretName()
        => Assert.Equal("sqlflow_azure", DuckDbAzureSecret.SecretName);

    [Theory]
    [InlineData("CREATE SECRET mine (TYPE AZURE)")]
    [InlineData("CREATE TEMPORARY SECRET mine (TYPE AZURE)")]
    [InlineData("CREATE PERSISTENT SECRET mine (TYPE AZURE)")]
    [InlineData("create or replace secret mine (type azure)")]
    public void AzureSecret_UserDeclaresSecretInInit_SuppressesGeneration(string init)
        => Assert.Empty(DuckDbAzureSecret.StatementsFor(
            MakeSource("abfss://d@acct.dfs.core.windows.net/t", ("init", init)),
            new FixedCredentialProvider(MakeCredential(CloudAuthMode.DefaultChain))));

    [Fact]
    public void AzureSecret_InitWithSecretAsIdentifierNotKeyword_StillGenerates()
    {
        // The word "secret" as a column/identifier (not a CREATE SECRET declaration) must not suppress generation.
        var statements = DuckDbAzureSecret.StatementsFor(
            MakeSource("abfss://d@acct.dfs.core.windows.net/t", ("init", "SET memory_limit = '1GB'")),
            new FixedCredentialProvider(MakeCredential(CloudAuthMode.DefaultChain)));
        Assert.Single(statements);
    }

    [Fact]
    public void AzureSecret_NonAzureLocationWithProvider_EmptyWhenProviderDeclines()
    {
        // The provider returns null for a non-Azure location, so no secret is generated.
        Assert.Empty(DuckDbAzureSecret.StatementsFor(
            MakeSource("/local/x.parquet"),
            new FixedCredentialProvider(null)));
    }
}
