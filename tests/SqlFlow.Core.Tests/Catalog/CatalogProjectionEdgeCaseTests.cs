using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// Additional, non-overlapping edge-case coverage for the pure catalog projections (no EF, no database, no clock).
/// These probe the boundaries the happy-path suite does not: repo-scoped identity determinism and cross-repo
/// distinctness, hash stability for empty/unicode/whitespace text, case-insensitive and wrong-type header parsing,
/// the duration unit fallbacks, best-effort metric degradation to null/0, path last-segment derivation, statement
/// step truncation and ordinal contiguity, and the edge/dependency/object projections. Every input is fixed so the
/// assertions are deterministic.
/// </summary>
public sealed class CatalogProjectionEdgeCaseTests
{
    private static readonly Guid RepoA = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RepoB = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FixedRunId = new("12121212-1212-1212-1212-121212121212");

    private static JsonElement Doc(string text) => JsonDocument.Parse(text).RootElement;

    // A minimal valid run.json header with the kind and an embedded result body, so a test can focus on one field.
    private static JsonElement RunDoc(string flowKind, string resultBody) => Doc($$"""
        {
          "flowKind": "{{flowKind}}",
          "flowName": "edge",
          "runId": "12121212-1212-1212-1212-121212121212",
          "success": true,
          "writtenUtc": "2026-06-17T10:00:00Z",
          "result": {{resultBody}}
        }
        """);

    // -------- CatalogIdentity: repo-scoped determinism and distinctness --------

    [Fact]
    public void Identity_SameRepoSameName_IsDeterministicAcrossCalls()
    {
        Assert.Equal(CatalogIdentity.Pipeline(RepoA, "orders"), CatalogIdentity.Pipeline(RepoA, "orders"));
    }

    [Fact]
    public void Identity_DifferentReposSameName_AreDistinctPipelines()
    {
        Assert.NotEqual(CatalogIdentity.Pipeline(RepoA, "orders"), CatalogIdentity.Pipeline(RepoB, "orders"));
    }

    [Fact]
    public void Identity_SameRepoDifferentNames_AreDistinct()
    {
        Assert.NotEqual(CatalogIdentity.Pipeline(RepoA, "orders"), CatalogIdentity.Pipeline(RepoA, "customers"));
    }

    [Theory]
    [InlineData("orders", "Orders")]
    [InlineData("dbo.customer", "DBO.Customer")]
    [InlineData("a", "A")]
    public void Identity_IsCaseSensitiveOnFlowName(string lower, string upper)
    {
        Assert.NotEqual(CatalogIdentity.Pipeline(RepoA, lower), CatalogIdentity.Pipeline(RepoA, upper));
    }

    [Fact]
    public void Identity_MatchesRepoScopedNameComposition()
    {
        // The id is the flow identity of the composed "{repoId:N}/{name}" string, so the projection joins to it.
        var expected = FlowIdentity.FromName($"{RepoA:N}/orders");
        Assert.Equal(expected, CatalogIdentity.Pipeline(RepoA, "orders"));
    }

    [Fact]
    public void Identity_EmptyRepoGuid_StillDeterministicAndScoped()
    {
        Assert.Equal(FlowIdentity.FromName($"{Guid.Empty:N}/orders"), CatalogIdentity.Pipeline(Guid.Empty, "orders"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Identity_BlankFlowName_Throws(string flowName)
    {
        Assert.Throws<ArgumentException>(() => CatalogIdentity.Pipeline(RepoA, flowName));
    }

    [Fact]
    public void Identity_NullFlowName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogIdentity.Pipeline(RepoA, null!));
    }

    // -------- Hash: stability and edge inputs --------

    [Fact]
    public void Hash_OfEmptyString_IsTheKnownSha256()
    {
        // The published SHA-256 of the empty byte sequence, lowercase hex.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", CatalogProjection.Hash(string.Empty));
    }

    [Fact]
    public void Hash_OfNull_DegradesToEmptyStringHash()
    {
        Assert.Equal(CatalogProjection.Hash(string.Empty), CatalogProjection.Hash(null!));
    }

    [Fact]
    public void Hash_DistinguishesWhitespaceOnlyDifferences()
    {
        Assert.NotEqual(CatalogProjection.Hash("a b"), CatalogProjection.Hash("a  b"));
    }

    [Fact]
    public void Hash_IsCaseSensitive()
    {
        Assert.NotEqual(CatalogProjection.Hash("Orders"), CatalogProjection.Hash("orders"));
    }

    [Fact]
    public void Hash_OfUnicode_IsStableAndUtf8Based()
    {
        var text = "navn: kunder æøå 中文 \U0001F600";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, CatalogProjection.Hash(text));
        Assert.Equal(64, CatalogProjection.Hash(text).Length);
    }

    // -------- RunFromJson: header parsing across kinds and degradation --------

    [Theory]
    [InlineData("FlowName", "FlowKind")]
    [InlineData("FLOWNAME", "FLOWKIND")]
    [InlineData("flowname", "flowkind")]
    public void RunFromJson_HeaderPropertyLookupIsCaseInsensitive(string nameKey, string kindKey)
    {
        var run = CatalogProjection.RunFromJson(Doc($$"""
            {
              "{{nameKey}}": "orders",
              "{{kindKey}}": "file",
              "runId": "12121212-1212-1212-1212-121212121212"
            }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Equal("orders", run!.FlowName);
        Assert.Equal("file", run.FlowKind);
    }

    [Fact]
    public void RunFromJson_RunIdNotAGuidString_ReturnsNull()
    {
        Assert.Null(CatalogProjection.RunFromJson(Doc("""
            { "flowKind": "file", "flowName": "orders", "runId": "not-a-guid" }
            """), RepoA));
    }

    [Fact]
    public void RunFromJson_RunIdWrongType_ReturnsNull()
    {
        // A non-string runId (here a JSON number) is a corrupt/foreign header. Per RunFromJson's contract it must
        // be skipped and return null, exactly like the non-guid string case. Today JsonElement.TryGetGuid throws
        // InvalidOperationException on a non-string element, so the projection crashes instead of degrading; this
        // asserts the correct (null) behavior. Recorded as a suspected bug for the triage loop to fix at source.
        Assert.Null(CatalogProjection.RunFromJson(Doc("""
            { "flowKind": "file", "flowName": "orders", "runId": 12345 }
            """), RepoA));
    }

    [Theory]
    [InlineData("\"   \"", "\"file\"")] // whitespace flowName
    [InlineData("\"orders\"", "\"   \"")] // whitespace flowKind
    [InlineData("\"\"", "\"file\"")] // empty flowName
    public void RunFromJson_BlankHeaderFields_ReturnNull(string flowName, string flowKind)
    {
        Assert.Null(CatalogProjection.RunFromJson(Doc($$"""
            { "flowName": {{flowName}}, "flowKind": {{flowKind}}, "runId": "12121212-1212-1212-1212-121212121212" }
            """), RepoA));
    }

    [Theory]
    [InlineData("{ \"flowKind\": \"file\", \"flowName\": \"x\", \"runId\": \"12121212-1212-1212-1212-121212121212\", \"success\": \"true\" }")] // string, not bool
    [InlineData("{ \"flowKind\": \"file\", \"flowName\": \"x\", \"runId\": \"12121212-1212-1212-1212-121212121212\", \"success\": 1 }")]      // number, not bool
    [InlineData("{ \"flowKind\": \"file\", \"flowName\": \"x\", \"runId\": \"12121212-1212-1212-1212-121212121212\" }")]                      // absent
    public void RunFromJson_SuccessMissingOrWrongType_DefaultsToFalse(string json)
    {
        var run = CatalogProjection.RunFromJson(Doc(json), RepoA);
        Assert.NotNull(run);
        Assert.False(run!.Success);
    }

    [Fact]
    public void RunFromJson_NoResult_LeavesMetricsNullAndCountsAtDefault()
    {
        var run = CatalogProjection.RunFromJson(Doc("""
            { "flowKind": "sp", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212", "success": true }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Null(run!.DurationSeconds);
        Assert.Null(run.RowsLoaded);
        Assert.Null(run.RowsInserted);
        Assert.Null(run.RowsUpdated);
        Assert.Null(run.RowsDeleted);
        Assert.Null(run.StartUtc);
        Assert.Null(run.EndUtc);
        Assert.Null(run.Error);
        Assert.Null(run.Host);
        Assert.Equal(0, run.SchemaVersion);
    }

    [Fact]
    public void RunFromJson_MissingWrittenUtc_IsDefaultDateTime()
    {
        var run = CatalogProjection.RunFromJson(Doc("""
            { "flowKind": "file", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212", "success": true }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Equal(default, run!.WrittenUtc);
    }

    [Fact]
    public void RunFromJson_RowsLoadedFallsBackToTotalRows()
    {
        var run = CatalogProjection.RunFromJson(RunDoc("ing", """{ "totalRows": 77 }"""), RepoA);
        Assert.NotNull(run);
        Assert.Equal(77, run!.RowsLoaded);
    }

    [Fact]
    public void RunFromJson_RowsLoadedPreferredOverTotalRows()
    {
        var run = CatalogProjection.RunFromJson(RunDoc("ing", """{ "rowsLoaded": 5, "totalRows": 99 }"""), RepoA);
        Assert.NotNull(run);
        Assert.Equal(5, run!.RowsLoaded);
    }

    [Fact]
    public void RunFromJson_DurationSecondsPreferredOverTotalMs()
    {
        var run = CatalogProjection.RunFromJson(RunDoc("ing", """{ "durationSeconds": 9, "totalMs": 1000 }"""), RepoA);
        Assert.NotNull(run);
        Assert.Equal(9, run!.DurationSeconds);
    }

    [Fact]
    public void RunFromJson_TotalMsAsNonNumber_YieldsNullDuration()
    {
        // totalMs must be a JSON number; a string is ignored rather than parsed.
        var run = CatalogProjection.RunFromJson(RunDoc("file", """{ "totalMs": "1500" }"""), RepoA);
        Assert.NotNull(run);
        Assert.Null(run!.DurationSeconds);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(250, 0.25)]
    [InlineData(2500, 2.5)]
    public void RunFromJson_TotalMsConvertsToSeconds(int totalMs, double expectedSeconds)
    {
        var run = CatalogProjection.RunFromJson(RunDoc("file", $$"""{ "totalMs": {{totalMs}} }"""), RepoA);
        Assert.NotNull(run);
        Assert.Equal(expectedSeconds, run!.DurationSeconds);
    }

    [Fact]
    public void RunFromJson_FractionalDurationSecondsWithoutTotalMs_YieldsNull()
    {
        // durationSeconds is read as an integer; a fractional value is not an Int64 and currently drops to null
        // (there is no totalMs fallback here), so the duration is lost. Asserting the observed behavior.
        var run = CatalogProjection.RunFromJson(RunDoc("ing", """{ "durationSeconds": 5.5 }"""), RepoA);
        Assert.NotNull(run);
        Assert.Null(run!.DurationSeconds);
    }

    [Fact]
    public void RunFromJson_NegativeSchemaVersion_PassesThroughUnclamped()
    {
        // Int() only guards the upper (overflow) bound; a negative value is already within int range and is kept.
        var run = CatalogProjection.RunFromJson(Doc("""
            { "flowKind": "file", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212", "schemaVersion": -3 }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Equal(-3, run!.SchemaVersion);
    }

    [Fact]
    public void RunFromJson_RowsInsertedBeyondInt64Range_DegradesToNull()
    {
        // A value past Int64 range is not a long; the granular count degrades to null rather than throwing.
        var run = CatalogProjection.RunFromJson(RunDoc("ing", """{ "rowsInserted": 99999999999999999999 }"""), RepoA);
        Assert.NotNull(run);
        Assert.Null(run!.RowsInserted);
    }

    [Fact]
    public void RunFromJson_StartAndEndUtcReadFromResultNotRoot()
    {
        // The timestamps live under result; a root-level copy must not be picked up.
        var run = CatalogProjection.RunFromJson(Doc("""
            {
              "flowKind": "ing", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212",
              "startTimeUtc": "2000-01-01T00:00:00Z", "endTimeUtc": "2000-01-01T00:00:00Z",
              "result": {}
            }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Null(run!.StartUtc);
        Assert.Null(run.EndUtc);
    }

    [Fact]
    public void RunFromJson_PipelineLinkIsRepoScoped()
    {
        var run = CatalogProjection.RunFromJson(RunDoc("file", "{}"), RepoB);
        Assert.NotNull(run);
        Assert.Equal(CatalogIdentity.Pipeline(RepoB, "edge"), run!.PipelineId);
        Assert.NotEqual(CatalogIdentity.Pipeline(RepoA, "edge"), run.PipelineId);
    }

    [Fact]
    public void RunFromJson_BlankErrorAndHost_DegradeToNull()
    {
        var run = CatalogProjection.RunFromJson(Doc("""
            {
              "flowKind": "file", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212",
              "error": "   ", "host": ""
            }
            """), RepoA);

        Assert.NotNull(run);
        Assert.Null(run!.Error);
        Assert.Null(run.Host);
    }

    // -------- RunFiles: processed inputs and export outputs --------

    [Fact]
    public void RunFiles_ProcessedFileWithBlankName_IsSkipped()
    {
        Assert.Empty(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "   ", "rows": 1, "columns": 1 } ] }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunFiles_ProcessedMissingCounts_DefaultToZero()
    {
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "a.csv" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(0, file.Rows);
        Assert.Equal(0, file.Columns);
        Assert.Equal(0, file.SizeBytes);
        Assert.Null(file.Path);
    }

    [Fact]
    public void RunFiles_NegativeColumnCount_PassesThroughUnclamped()
    {
        // Int() guards only the upper bound; a negative column count stays negative.
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "a.csv", "columns": -1 } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(-1, file.Columns);
    }

    [Fact]
    public void RunFiles_FractionalColumnCount_DegradesToZero()
    {
        // A non-integer column count is not an Int64; it degrades to 0 rather than truncating.
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "a.csv", "columns": 3.5 } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(0, file.Columns);
    }

    [Fact]
    public void RunFiles_RowsBeyondInt64Range_DegradesToZero()
    {
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "a.csv", "rows": 99999999999999999999 } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(0, file.Rows);
    }

    [Fact]
    public void RunFiles_ProcessedFilesNotArray_IsIgnored()
    {
        Assert.Empty(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": { "name": "a.csv" } }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunFiles_ProcessedThenExport_BothMappedInOrder()
    {
        var files = CatalogProjection.RunFiles(RunDoc("file", """
            {
              "processedFiles": [ { "name": "in.csv", "rows": 1, "columns": 2 } ],
              "files": [ { "path": "/out/out.csv", "rows": 3, "bytes": 9 } ]
            }
            """), FixedRunId, RepoA);

        Assert.Equal(2, files.Count);
        Assert.Equal("in.csv", files[0].Name);
        Assert.Equal(2, files[0].Columns);
        Assert.Equal("out.csv", files[1].Name);
        Assert.Equal(0, files[1].Columns); // exports report no columns
        Assert.Equal(9, files[1].SizeBytes);
    }

    [Fact]
    public void RunFiles_ExportWithBlankPath_IsSkipped()
    {
        Assert.Empty(CatalogProjection.RunFiles(
            RunDoc("exp", """{ "files": [ { "path": "   ", "rows": 1 } ] }"""),
            FixedRunId, RepoA));
    }

    [Theory]
    [InlineData("/var/data/orders.csv", "orders.csv")]
    [InlineData("C:\\out\\orders.csv", "orders.csv")]
    [InlineData("a/b\\c.parquet", "c.parquet")] // mixed separators, last wins
    [InlineData("orders.csv", "orders.csv")] // no separator, whole path
    [InlineData("/", "/")] // only a separator, falls back to whole path
    [InlineData("C:\\out\\", "C:\\out\\")] // trailing separator, falls back to whole path
    public void RunFiles_ExportFileName_IsLastPathSegment(string path, string expectedName)
    {
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("exp", $$"""{ "files": [ { "path": {{JsonString(path)}}, "rows": 1 } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(expectedName, file.Name);
        Assert.Equal(path, file.Path);
    }

    [Fact]
    public void RunFiles_CarriesRunAndRepoIdentity()
    {
        var file = Assert.Single(CatalogProjection.RunFiles(
            RunDoc("file", """{ "processedFiles": [ { "name": "a.csv" } ] }"""),
            FixedRunId, RepoB));

        Assert.Equal(FixedRunId, file.RunId);
        Assert.Equal(RepoB, file.RepoId);
    }

    // -------- RunStatements: ordering, defaults, truncation, non-string entries --------

    [Fact]
    public void RunStatements_TraceEntryWithoutStep_DefaultsStepToSql()
    {
        var statement = Assert.Single(CatalogProjection.RunStatements(
            RunDoc("ing", """{ "sqlTrace": [ { "sql": "SELECT 1" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("sql", statement.Step);
        Assert.Equal("SELECT 1", statement.Sql);
        Assert.Equal(1, statement.Ordinal);
    }

    [Fact]
    public void RunStatements_BlankSqlEntriesAreSkipped_OrdinalsStayContiguous()
    {
        var statements = CatalogProjection.RunStatements(RunDoc("ing", """
            {
              "sqlTrace": [
                { "step": "one", "sql": "A" },
                { "step": "blank", "sql": "   " },
                { "step": "missing" },
                { "step": "two", "sql": "B" }
              ]
            }
            """), FixedRunId, RepoA);

        Assert.Equal(2, statements.Count);
        Assert.Equal(new[] { 1, 2 }, statements.Select(s => s.Ordinal));
        Assert.Equal("one", statements[0].Step);
        Assert.Equal("two", statements[1].Step);
    }

    [Fact]
    public void RunStatements_NonObjectTraceEntriesContributeNothing()
    {
        var statement = Assert.Single(CatalogProjection.RunStatements(
            RunDoc("ing", """{ "sqlTrace": [ "raw string entry", 42, { "step": "real", "sql": "X" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("real", statement.Step);
        Assert.Equal(1, statement.Ordinal);
    }

    [Fact]
    public void RunStatements_DdlNonStringEntriesAreSkipped()
    {
        var statement = Assert.Single(CatalogProjection.RunStatements(
            RunDoc("file", """{ "ddlExecuted": [ 1, true, null, "ALTER TABLE t ADD c int" ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("schema.ddl", statement.Step);
        Assert.Equal("ALTER TABLE t ADD c int", statement.Sql);
    }

    [Fact]
    public void RunStatements_LongStep_IsTruncatedTo128Chars()
    {
        var longStep = new string('s', 200);
        var statement = Assert.Single(CatalogProjection.RunStatements(
            RunDoc("ing", $$"""{ "sqlTrace": [ { "step": "{{longStep}}", "sql": "X" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(128, statement.Step.Length);
        Assert.Equal(new string('s', 128), statement.Step);
    }

    [Fact]
    public void RunStatements_StepExactly128Chars_IsNotTruncated()
    {
        var step = new string('s', 128);
        var statement = Assert.Single(CatalogProjection.RunStatements(
            RunDoc("ing", $$"""{ "sqlTrace": [ { "step": "{{step}}", "sql": "X" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal(128, statement.Step.Length);
    }

    [Fact]
    public void RunStatements_NoResult_IsEmpty()
    {
        Assert.Empty(CatalogProjection.RunStatements(
            Doc("""{ "flowKind": "ing", "flowName": "x", "runId": "12121212-1212-1212-1212-121212121212" }"""),
            FixedRunId, RepoA));
    }

    // -------- RunSurrogateKeys / RunHealthCheckMetrics / RunAssertions: skip and default rules --------

    [Fact]
    public void RunSurrogateKeys_EntryMissingTable_IsSkipped()
    {
        Assert.Empty(CatalogProjection.RunSurrogateKeys(
            RunDoc("ing", """{ "surrogateKeys": [ { "surrogateColumn": "Key", "keysGenerated": 1 } ] }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunSurrogateKeys_NotAnArray_IsEmpty()
    {
        Assert.Empty(CatalogProjection.RunSurrogateKeys(
            RunDoc("ing", """{ "surrogateKeys": { "surrogateTable": "T" } }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunSurrogateKeys_OnlyTable_FillsDefaults()
    {
        var key = Assert.Single(CatalogProjection.RunSurrogateKeys(
            RunDoc("ing", """{ "surrogateKeys": [ { "surrogateTable": "DW.dim.X" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("DW.dim.X", key.SurrogateTable);
        Assert.Equal(0, key.SurrogateKeyId);
        Assert.Equal(string.Empty, key.SurrogateColumn);
        Assert.False(key.IsRemote);
        Assert.Equal(0, key.KeysGenerated);
        Assert.Equal(0, key.RowsStamped);
        Assert.False(key.Executed);
        Assert.Null(key.Error);
    }

    [Fact]
    public void RunHealthCheckMetrics_EntryMissingName_IsSkipped()
    {
        Assert.Empty(CatalogProjection.RunHealthCheckMetrics(
            RunDoc("hc", """{ "metricResults": [ { "anomalies": 3 } ] }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunHealthCheckMetrics_NotAnArray_IsEmpty()
    {
        Assert.Empty(CatalogProjection.RunHealthCheckMetrics(
            RunDoc("hc", """{ "metricResults": "RowCount" }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunHealthCheckMetrics_OnlyName_FillsCountDefaults()
    {
        var metric = Assert.Single(CatalogProjection.RunHealthCheckMetrics(
            RunDoc("hc", """{ "metricResults": [ { "name": "RowCount" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("RowCount", metric.Name);
        Assert.Equal(0, metric.SeriesPoints);
        Assert.Equal(0, metric.ImputedPoints);
        Assert.Equal(0, metric.ImmaturePoints);
        Assert.Equal(0, metric.Anomalies);
        Assert.Equal(0, metric.LevelShifts);
        Assert.False(metric.ModelTrained);
        Assert.Null(metric.ModelTrainer);
        Assert.Null(metric.Error);
    }

    [Fact]
    public void RunAssertions_EntryMissingName_IsSkipped()
    {
        Assert.Empty(CatalogProjection.RunAssertions(
            RunDoc("file", """{ "assertions": [ { "result": "3" } ] }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunAssertions_NotAnArray_IsEmpty()
    {
        Assert.Empty(CatalogProjection.RunAssertions(
            RunDoc("file", """{ "assertions": {} }"""),
            FixedRunId, RepoA));
    }

    [Fact]
    public void RunAssertions_OnlyName_FillsStringDefaults()
    {
        var assertion = Assert.Single(CatalogProjection.RunAssertions(
            RunDoc("file", """{ "assertions": [ { "name": "rowcount" } ] }"""),
            FixedRunId, RepoA));

        Assert.Equal("rowcount", assertion.Name);
        Assert.Equal(string.Empty, assertion.Result);
        Assert.Equal(string.Empty, assertion.AssertedValue);
        Assert.False(assertion.Evaluated);
        Assert.Null(assertion.Error);
    }

    // -------- Edge / FlowDependency / MapObject projections --------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Edge_BlankFlow_HasNullFlowAndPipeline(string flow)
    {
        var edge = new LineageEdge { Flow = flow, Relation = LineageRelation.Reads, ObjectKey = "k", Tier = LineageTier.Observed };
        var projected = CatalogProjection.Edge(edge, RepoA, "Customer");

        Assert.Null(projected.Flow);
        Assert.Null(projected.PipelineId);
        Assert.Equal("Customer", projected.ObjectName);
    }

    [Fact]
    public void Edge_NullObjectName_FallsBackToObjectKey()
    {
        var edge = new LineageEdge { Flow = "orders", Relation = LineageRelation.Writes, ObjectKey = "srv|db|dbo|t", Tier = LineageTier.Declared };
        var projected = CatalogProjection.Edge(edge, RepoA, null!);

        Assert.Equal("srv|db|dbo|t", projected.ObjectName);
    }

    [Theory]
    [InlineData(LineageRelation.Reads, "Reads")]
    [InlineData(LineageRelation.Writes, "Writes")]
    [InlineData(LineageRelation.Creates, "Creates")]
    [InlineData(LineageRelation.Requires, "Requires")]
    [InlineData(LineageRelation.Destroys, "Destroys")]
    public void Edge_RelationRendersEnumName(LineageRelation relation, string expected)
    {
        var edge = new LineageEdge { Flow = "f", Relation = relation, ObjectKey = "k", Tier = LineageTier.Declared };
        Assert.Equal(expected, CatalogProjection.Edge(edge, RepoA, "n").Relation);
    }

    [Theory]
    [InlineData(LineageTier.Declared, "Declared")]
    [InlineData(LineageTier.Observed, "Observed")]
    [InlineData(LineageTier.Derived, "Derived")]
    public void Edge_TierRendersEnumName(LineageTier tier, string expected)
    {
        var edge = new LineageEdge { Flow = "f", Relation = LineageRelation.Reads, ObjectKey = "k", Tier = tier };
        Assert.Equal(expected, CatalogProjection.Edge(edge, RepoA, "n").Tier);
    }

    [Fact]
    public void Edge_BlankViaModule_DegradesToNull()
    {
        var edge = new LineageEdge { ViaModule = "   ", Relation = LineageRelation.Reads, ObjectKey = "k", Tier = LineageTier.Derived };
        Assert.Null(CatalogProjection.Edge(edge, RepoA, "n").ViaModule);
    }

    [Fact]
    public void Edge_NullEdge_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogProjection.Edge(null!, RepoA, "n"));
    }

    [Fact]
    public void FlowDependency_NullViaObjects_DegradesToEmptyString()
    {
        var dependency = new LineageFlowDependency { FromFlow = "a", ToFlow = "b", ViaObjects = ["k"] };
        var projected = CatalogProjection.FlowDependency(dependency, RepoA, null!);

        Assert.Equal(string.Empty, projected.ViaObjects);
    }

    [Fact]
    public void FlowDependency_EndpointsAreRepoScopedAndDistinct()
    {
        var dependency = new LineageFlowDependency { FromFlow = "a", ToFlow = "b", ViaObjects = ["k"] };
        var projected = CatalogProjection.FlowDependency(dependency, RepoA, "Staging");

        Assert.Equal(CatalogIdentity.Pipeline(RepoA, "a"), projected.FromPipelineId);
        Assert.Equal(CatalogIdentity.Pipeline(RepoA, "b"), projected.ToPipelineId);
        Assert.NotEqual(projected.FromPipelineId, projected.ToPipelineId);
    }

    [Fact]
    public void FlowDependency_NullDependency_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogProjection.FlowDependency(null!, RepoA, "n"));
    }

    [Theory]
    [InlineData(LineageNodeKind.Unknown, "Unknown")]
    [InlineData(LineageNodeKind.Table, "Table")]
    [InlineData(LineageNodeKind.View, "View")]
    [InlineData(LineageNodeKind.Procedure, "Procedure")]
    [InlineData(LineageNodeKind.Function, "Function")]
    [InlineData(LineageNodeKind.Trigger, "Trigger")]
    [InlineData(LineageNodeKind.Synonym, "Synonym")]
    [InlineData(LineageNodeKind.File, "File")]
    public void MapObject_KindRendersEnumName(LineageNodeKind kind, string expected)
    {
        var node = new LineageObjectNode { Key = "k", ServerRef = "srv", Name = "n", Kind = kind };
        Assert.Equal(expected, CatalogProjection.MapObject(node, FixedUtc()).Kind);
    }

    [Fact]
    public void MapObject_BlankDatabaseSchemaDefinition_DegradeToNull()
    {
        var node = new LineageObjectNode
        {
            Key = "k", ServerRef = "srv", Name = "n", Kind = LineageNodeKind.Table,
            Database = "   ", Schema = "", Definition = "   ",
        };
        var o = CatalogProjection.MapObject(node, FixedUtc());

        Assert.Null(o.Database);
        Assert.Null(o.Schema);
        Assert.Null(o.Definition);
    }

    [Fact]
    public void MapObject_StampsBothTimestampsAtNowUtc()
    {
        var now = FixedUtc();
        var node = new LineageObjectNode { Key = "k", ServerRef = "srv", Name = "n", Kind = LineageNodeKind.Table };
        var o = CatalogProjection.MapObject(node, now);

        Assert.Equal(now, o.FirstSeenUtc);
        Assert.Equal(now, o.LastSeenUtc);
    }

    [Fact]
    public void MapObject_NullNode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogProjection.MapObject(null!, FixedUtc()));
    }

    // -------- Pipeline projection: null and blank fallbacks --------

    [Fact]
    public void Pipeline_NullNonNullableFields_DegradeToEmptyString()
    {
        var pipeline = CatalogProjection.Pipeline(
            RepoA, "orders", null!, batch: null, null!, null, null, null!, null!, null!, FixedUtc());

        Assert.Equal(string.Empty, pipeline.Kind);
        Assert.Equal(string.Empty, pipeline.RelativePath);
        Assert.Equal(string.Empty, pipeline.ContentHash);
        Assert.Equal(string.Empty, pipeline.Yaml);
        Assert.Equal(string.Empty, pipeline.DefinitionJson);
        Assert.Null(pipeline.Batch);
        Assert.Null(pipeline.SourceServer);
        Assert.Null(pipeline.TargetServer);
    }

    [Fact]
    public void Pipeline_BlankBatchAndServers_DegradeToNull()
    {
        var pipeline = CatalogProjection.Pipeline(
            RepoA, "orders", "file", batch: "   ", "flows/o.yaml", "  ", "\t", "hash", "yaml", "{}", FixedUtc());

        Assert.Null(pipeline.Batch);
        Assert.Null(pipeline.SourceServer);
        Assert.Null(pipeline.TargetServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Pipeline_BlankName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => CatalogProjection.Pipeline(
            RepoA, name, "file", null, "p", null, null, "h", "y", "{}", FixedUtc()));
    }

    [Fact]
    public void Pipeline_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogProjection.Pipeline(
            RepoA, null!, "file", null, "p", null, null, "h", "y", "{}", FixedUtc()));
    }

    [Fact]
    public void Pipeline_IsActiveAndStampsBothTimestamps()
    {
        var now = FixedUtc();
        var pipeline = CatalogProjection.Pipeline(
            RepoA, "orders", "file", null, "p", null, null, "h", "y", "{}", now);

        Assert.True(pipeline.Active);
        Assert.Equal(now, pipeline.FirstSeenUtc);
        Assert.Equal(now, pipeline.LastSeenUtc);
    }

    // -------- private helpers (uniquely named so they cannot collide in the namespace) --------

    private static DateTime FixedUtc() => new(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Encodes a string as a JSON string literal so a path with backslashes embeds safely into a document.</summary>
    private static string JsonString(string value) => JsonSerializer.Serialize(value);
}
