using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Providers.MySql;
using SqlFlow.Providers.Postgres;
using SqlFlow.SqlServer;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge cases for the MiscCore surface: RelationalObject multipart parsing and round-trips,
/// FlowIdentity determinism and collision resistance, RunHistoryWriter SafeName and artifact writing, RunLogger
/// level boundaries and snapshot semantics, the SourceProvider registry seams (including the SQL Server
/// canonicalizer the existing suite does not exercise), and YamlFlowLoader validation messages. Everything here
/// is pure and in-memory, so the suite runs everywhere with no database.
/// </summary>
public sealed class MiscCoreEdgeCaseTests
{
    private static CatalogColumn MiscCol(string nativeType) => new() { Name = "C", Ordinal = 1, NativeType = nativeType };

    private static ResolvedConnection MiscResolved(DataSourceKind kind) => new()
    {
        Kind = kind,
        CanonicalString = "Server=x;Database=y;",
        RedactedString = "Server=x;Database=y;",
    };

    // ---- RelationalObject: multipart and bracket edges beyond the existing three/four-part cases ----

    [Fact]
    public void RelationalObject_FivePart_KeepsRightmostThree()
    {
        var o = RelationalObject.Parse("linked.extra.DW.dbo.Sales");
        Assert.Equal("DW", o.Database);
        Assert.Equal("dbo", o.Schema);
        Assert.Equal("Sales", o.Name);
    }

    [Fact]
    public void RelationalObject_MixedBracketedAndBareParts_DropsLeadingServer()
    {
        var o = RelationalObject.Parse("[srv].DW.dbo.[Sales]");
        Assert.Equal("DW", o.Database);
        Assert.Equal("dbo", o.Schema);
        Assert.Equal("Sales", o.Name);
    }

    [Theory]
    [InlineData(".dbo.Sales")]          // empty leading (database) part
    [InlineData("DW.dbo.Sales.")]       // trailing dot makes the rightmost object part empty
    [InlineData("DW. .Sales")]          // whitespace-only middle part trims to empty
    [InlineData("[].[dbo].[Sales]")]    // empty bracketed database part
    public void RelationalObject_EmptyPartAfterTrim_Throws(string name)
        => Assert.Throws<SqlFlowException>(() => RelationalObject.Parse(name));

    [Fact]
    public void RelationalObject_BracketedPartHoldingDelimiterDots_CountsAsOnePart()
    {
        // The two dots live inside one bracketed object name, so this is a two-part name and must throw,
        // not be mistaken for a four-part name.
        var ex = Assert.Throws<SqlFlowException>(() => RelationalObject.Parse("dbo.[a.b.c]"));
        Assert.Contains("2 part", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RelationalObject_QualifiedName_RoundTripsThroughParse()
    {
        var original = RelationalObject.Parse("[my.db].[dbo].[Weird]]Name]");
        var reparsed = RelationalObject.Parse(original.QualifiedName);
        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void RelationalObject_QualifiedName_AlwaysRebracketsBarePartsAndKeepsDots()
    {
        var o = RelationalObject.Parse("DW.dbo.Sales");
        Assert.Equal("[DW].[dbo].[Sales]", o.QualifiedName);
    }

    [Fact]
    public void RelationalObject_OnlyDots_IsTreatedAsEmptyParts_Throws()
        => Assert.Throws<SqlFlowException>(() => RelationalObject.Parse("..."));

    // ---- FlowIdentity: determinism, case sensitivity, whitespace handling, collision resistance ----

    [Fact]
    public void FlowIdentity_IsCaseSensitive_DifferentCasingDifferentId()
        => Assert.NotEqual(FlowIdentity.FromName("Orders"), FlowIdentity.FromName("orders"));

    [Fact]
    public void FlowIdentity_LeadingAndTrailingSpaceIsSignificant()
    {
        Assert.NotEqual(FlowIdentity.FromName("orders"), FlowIdentity.FromName("orders "));
        Assert.NotEqual(FlowIdentity.FromName("orders"), FlowIdentity.FromName(" orders"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FlowIdentity_FromName_BlankOrWhitespace_Throws(string name)
        => Assert.Throws<ArgumentException>(() => FlowIdentity.FromName(name));

    [Fact]
    public void FlowIdentity_FromName_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => FlowIdentity.FromName(null!));

    [Fact]
    public void FlowIdentity_Unicode_IsDeterministicAndDistinct()
    {
        var first = FlowIdentity.FromName("ordrer-æøå");
        var second = FlowIdentity.FromName("ordrer-æøå");
        Assert.Equal(first, second);
        Assert.NotEqual(FlowIdentity.FromName("ordrer-aoa"), first);
    }

    [Fact]
    public void FlowIdentity_ManyNames_ProduceNoCollisions()
    {
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 2000; i++)
        {
            Assert.True(ids.Add(FlowIdentity.FromName("flow_" + i.ToString(CultureInfo.InvariantCulture))));
        }
    }

    [Fact]
    public void FlowIdentity_Resolve_BlankName_WithNoPinnedId_Throws()
        => Assert.Throws<ArgumentException>(() => FlowIdentity.Resolve(null, "   "));

    [Fact]
    public void FlowIdentity_Resolve_PinnedId_BypassesNameValidation()
    {
        // A pinned id is used verbatim and the name is never hashed, so even a blank name is accepted here.
        var pinned = new Guid("11112222-3333-4444-5555-666677778888");
        Assert.Equal(pinned, FlowIdentity.Resolve(pinned, "   "));
    }

    // ---- RunHistoryWriter.SafeName: invalid path chars, unicode, trimming, collapse-to-fallback ----

    [Fact]
    public void SafeName_AllInvalidChars_BecomeUnderscores_NotFlowFallback()
    {
        // Invalid filename characters are replaced one-for-one with underscores, so a name made only of them
        // stays non-empty; the "flow" fallback fires only when the sanitized result is empty (the all-whitespace
        // case), never here. Each interior control character survives Trim, so the length is preserved exactly.
        var invalid = Path.GetInvalidFileNameChars();
        var safe = RunHistoryWriter.SafeName(new string(invalid));
        Assert.Equal(new string('_', invalid.Length), safe);
    }

    [Fact]
    public void SafeName_WhitespaceOnly_FallsBackToFlow()
        => Assert.Equal("flow", RunHistoryWriter.SafeName("   "));

    [Fact]
    public void SafeName_TrimsThenSanitizes()
        => Assert.Equal("a_b", RunHistoryWriter.SafeName("  a/b  "));

    [Fact]
    public void SafeName_PreservesUnicodeLetters()
        => Assert.Equal("ordrer-æøå", RunHistoryWriter.SafeName("ordrer-æøå"));

    [Fact]
    public void SafeName_ReplacesEachInvalidCharIndividually()
    {
        // Three invalid characters become three underscores; valid characters are untouched.
        Assert.Equal("a_b_c_d", RunHistoryWriter.SafeName("a<b>c|d"));
    }

    [Fact]
    public void SafeName_IsIdempotent()
    {
        var once = RunHistoryWriter.SafeName("bad/name:here");
        Assert.Equal(once, RunHistoryWriter.SafeName(once));
    }

    // ---- RunHistoryWriter.Write: run-id prefix, empty content, multi-file layout ----

    [Fact]
    public void Write_FolderName_UsesFirstEightHexOfRunId()
    {
        var anchor = MiscTempDir();
        try
        {
            var runId = new Guid("0123456789abcdef0123456789abcdef");
            var dir = RunHistoryWriter.Write(anchor, "f", runId, new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                new Dictionary<string, string> { ["run.json"] = "{}" });

            Assert.EndsWith("20260610-010203_01234567", Path.GetFileName(dir), StringComparison.Ordinal);
        }
        finally
        {
            MiscCleanup(anchor);
        }
    }

    [Fact]
    public void Write_EmptyContent_WritesEmptyFile()
    {
        var anchor = MiscTempDir();
        try
        {
            var dir = RunHistoryWriter.Write(anchor, "f", Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string> { ["trace.sql"] = string.Empty });

            Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(dir, "trace.sql")));
        }
        finally
        {
            MiscCleanup(anchor);
        }
    }

    [Fact]
    public void Write_NoFiles_StillCreatesRunFolder()
    {
        var anchor = MiscTempDir();
        try
        {
            var dir = RunHistoryWriter.Write(anchor, "f", Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string>());

            Assert.True(Directory.Exists(dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            MiscCleanup(anchor);
        }
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Write_BlankAnchor_Throws(string anchor)
        => Assert.Throws<ArgumentException>(() => RunHistoryWriter.Write(
            anchor, "f", Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new Dictionary<string, string> { ["run.json"] = "{}" }));

    [Fact]
    public void Write_NullFiles_Throws()
    {
        var anchor = MiscTempDir();
        try
        {
            Assert.Throws<ArgumentNullException>(() => RunHistoryWriter.Write(
                anchor, "f", Guid.NewGuid(), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null!));
        }
        finally
        {
            MiscCleanup(anchor);
        }
    }

    // ---- RunLogger: level boundaries, validation, snapshot isolation, empty render ----

    [Fact]
    public void Logger_DebugLevel_KeepsInfoAndDebug_DropsTrace()
    {
        var logger = new RunLogger(RunLogLevel.Debug);
        logger.Log(RunLogLevel.Info, "i", "1");
        logger.Log(RunLogLevel.Debug, "d", "2");
        logger.Log(RunLogLevel.Trace, "t", "3");

        Assert.Equal(["i", "d"], logger.Entries.Select(e => e.Step));
    }

    [Fact]
    public void Logger_KeepsEntryAtExactlyTheEnabledLevel()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        logger.Log(RunLogLevel.Info, "i", "kept");
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Logger_EntriesProperty_ReturnsSnapshot_NotLiveList()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        logger.Log(RunLogLevel.Info, "a", "first");

        var snapshot = logger.Entries;
        logger.Log(RunLogLevel.Info, "b", "second");

        Assert.Single(snapshot);
        Assert.Equal(2, logger.Entries.Count);
    }

    [Fact]
    public void Logger_BlankStep_Throws()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        Assert.Throws<ArgumentException>(() => logger.Log(RunLogLevel.Info, "   ", "msg"));
    }

    [Fact]
    public void Logger_NullMessage_Throws()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        Assert.Throws<ArgumentNullException>(() => logger.Log(RunLogLevel.Info, "step", null!));
    }

    [Fact]
    public void Logger_DroppedEntry_DoesNotReachEcho()
    {
        var lines = new List<string>();
        var logger = new RunLogger(RunLogLevel.Info, lines.Add);
        logger.Log(RunLogLevel.Debug, "d", "dropped");
        Assert.Empty(lines);
    }

    [Fact]
    public void Logger_Render_EmptyLogger_IsEmptyString()
        => Assert.Equal(string.Empty, new RunLogger(RunLogLevel.Trace).Render());

    [Fact]
    public void Logger_Format_SingleLineMessage_HasNoContinuation()
    {
        var entry = new RunLogEntry
        {
            TimestampUtc = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc),
            Level = RunLogLevel.Info,
            Step = "stage.copy",
            Message = "done",
        };

        var formatted = RunLogger.Format(entry);
        Assert.DoesNotContain("\n", formatted, StringComparison.Ordinal);
        Assert.Contains("INFO", formatted, StringComparison.Ordinal);
        Assert.EndsWith("done", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_Format_NormalizesCarriageReturnLineEndings()
    {
        var entry = new RunLogEntry
        {
            TimestampUtc = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc),
            Level = RunLogLevel.Trace,
            Step = "upsert.insert",
            Message = "line1\r\nline2",
        };

        var lines = RunLogger.Format(entry).Split(Environment.NewLine);
        Assert.Equal("    | line2", lines[1]);
    }

    [Fact]
    public void Logger_Format_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => RunLogger.Format(null!));

    // ---- SourceProvider: the SQL Server canonicalizer the existing suite never exercises ----

    [Fact]
    public void SqlServerCanonicalizer_HandlesMssqlAndAzdb_NotForeignKinds()
    {
        var c = new SqlConnectionStringCanonicalizer();
        Assert.True(c.CanHandle(DataSourceKind.MSSQL));
        Assert.True(c.CanHandle(DataSourceKind.AZDB));
        Assert.False(c.CanHandle(DataSourceKind.MySQL));
        Assert.False(c.CanHandle(DataSourceKind.PostgreSQL));
    }

    [Fact]
    public void SqlServerCanonicalizer_IntegratedSecurity_PassesSelfAuthenticatingGate()
    {
        var canonical = new SqlConnectionStringCanonicalizer().Canonicalize(
            "Server=db;Database=erp;Integrated Security=true",
            ConnectionRole.Target,
            SecretlessPolicy.RequireSelfAuthenticating);

        Assert.Contains("SQLFlow Target", canonical.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerCanonicalizer_RestingPassword_IsRejected()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new SqlConnectionStringCanonicalizer().Canonicalize(
            "Server=db;Database=erp;User ID=u;Password=p",
            ConnectionRole.Source,
            SecretlessPolicy.RequireSelfAuthenticating));

        Assert.Contains("${...}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerCanonicalizer_Trusted_RedactsUserIdAndPassword()
    {
        var canonical = new SqlConnectionStringCanonicalizer().Canonicalize(
            "Server=db;Database=erp;User ID=sa;Password=secret",
            ConnectionRole.Source,
            SecretlessPolicy.Trusted);

        Assert.DoesNotContain("Password", canonical.Redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sa", canonical.Redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServerCanonicalizer_MalformedString_FailsClearly()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new SqlConnectionStringCanonicalizer().Canonicalize(
            "Server=db;Bogus", ConnectionRole.Source, SecretlessPolicy.Trusted));
        Assert.Contains("Malformed", ex.Message, StringComparison.Ordinal);
    }

    // ---- SourceProvider: SQL Server dialect and identity type mapper ----

    [Fact]
    public void SqlServerDialect_CanHandle_MssqlAndAzdb_NotForeign()
    {
        var dialect = new SqlServerSourceDialect();
        Assert.True(dialect.CanHandle(DataSourceKind.MSSQL));
        Assert.True(dialect.CanHandle(DataSourceKind.AZDB));
        Assert.False(dialect.CanHandle(DataSourceKind.MySQL));
    }

    [Fact]
    public void SqlServerTypeMapper_ReturnsNativeTypeVerbatim()
    {
        var mapper = new SqlServerSourceTypeMapper();
        Assert.Equal("decimal(18, 2)", mapper.ToSqlServerType(MiscCol("decimal(18, 2)")));
        Assert.Equal("nvarchar(max)", mapper.ToSqlServerType(MiscCol("nvarchar(max)")));
    }

    // ---- SourceProvider: composite registries with an empty registry and foreign kinds ----

    [Fact]
    public async Task CompositeConnectionFactory_EmptyRegistry_FailsClearly()
    {
        var composite = new CompositeConnectionFactory([]);
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => composite.OpenAsync(MiscResolved(DataSourceKind.MSSQL)));
        Assert.Contains("MSSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeCatalogReaderFactory_EmptyRegistry_Throws()
    {
        var composite = new CompositeCatalogReaderFactory([]);
        Assert.Throws<SqlFlowException>(() => composite.ReaderFor(MiscResolved(DataSourceKind.MySQL)));
    }

    // ---- SourceProvider: MySQL mapper boundaries distinct from the existing mapping rows ----

    [Theory]
    [InlineData("decimal(38,0)", "decimal(38, 0)")]   // exactly at the precision ceiling: allowed
    [InlineData("decimal", "decimal(10, 0)")]         // no args: the MySQL default precision
    [InlineData("char(4000)", "nchar(4000)")]         // exactly at the nchar ceiling
    [InlineData("char(4001)", "nvarchar(max)")]       // one over: spills to max
    [InlineData("binary(8000)", "binary(8000)")]      // exactly at the binary ceiling
    [InlineData("binary(8001)", "varbinary(max)")]    // one over: spills to max
    [InlineData("bit(8)", "binary(1)")]               // eight bits pack into one byte
    [InlineData("bit(9)", "binary(2)")]               // nine bits need two bytes
    [InlineData("bit", "bit")]                        // no args: a single bit
    [InlineData("boolean", "bit")]
    [InlineData("INT", "int")]                        // upper-case base is normalized
    public void MySqlMapper_BoundaryRows_Map(string mysql, string expected)
        => Assert.Equal(expected, new MySqlSourceTypeMapper().ToSqlServerType(MiscCol(mysql)));

    [Fact]
    public void MySqlMapper_PrecisionOneOverCeiling_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new MySqlSourceTypeMapper().ToSqlServerType(MiscCol("decimal(39,0)")));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- SourceProvider: PostgreSQL mapper boundaries distinct from the existing mapping rows ----

    [Theory]
    [InlineData("numeric(38,2)", "decimal(38, 2)")]   // exactly at the precision ceiling
    [InlineData("bpchar(4000)", "nchar(4000)")]       // exactly at the char ceiling
    [InlineData("bpchar(4001)", "nvarchar(max)")]     // one over: spills to max
    [InlineData("varchar(4001)", "nvarchar(max)")]    // one over for varchar: spills to max
    [InlineData("serial", "int")]                     // pseudo-type maps like int
    [InlineData("bigserial", "bigint")]
    [InlineData("_text", "nvarchar(max)")]            // array (leading underscore) rides as text
    [InlineData("_numeric", "nvarchar(max)")]         // array short-circuits before numeric precision check
    [InlineData("double precision", "float(53)")]     // multi-word base type
    public void PostgresMapper_BoundaryRows_Map(string postgres, string expected)
        => Assert.Equal(expected, new PostgresSourceTypeMapper().ToSqlServerType(MiscCol(postgres)));

    [Fact]
    public void PostgresMapper_PrecisionOneOverCeiling_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new PostgresSourceTypeMapper().ToSqlServerType(MiscCol("numeric(39,2)")));
        Assert.Contains("'C'", ex.Message, StringComparison.Ordinal);
    }

    // ---- YamlFlowLoader: validation messages and edges the existing suite does not cover ----

    [Fact]
    public void Yaml_MissingSource_NamesTheField()
    {
        const string yaml =
            """
            name: x
            target: { connection: c, schema: dbo, table: T }
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml));
        Assert.Contains("'source'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_MissingName_NamesTheField()
    {
        const string yaml =
            """
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml));
        Assert.Contains("'name'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_MissingSourceType_NamesTheNestedField()
    {
        const string yaml =
            """
            name: x
            source: { location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml));
        Assert.Contains("'source.type'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("schema")]
    [InlineData("table")]
    public void Yaml_MissingTargetSubField_NamesThatField(string missing)
    {
        var parts = new List<string> { "connection: c", "schema: dbo", "table: T" };
        parts.RemoveAll(p => p.StartsWith(missing, StringComparison.Ordinal));
        var yaml =
            $$"""
            name: x
            source: { type: csv, location: ./x.csv }
            target: { {{string.Join(", ", parts)}} }
            """;

        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml));
        Assert.Contains($"'target.{missing}'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_EmptyDocument_ReportsEmpty()
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(""));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Yaml_BlankPath_Throws()
        => Assert.Throws<ArgumentException>(() => new YamlFlowLoader().LoadFile("   "));

    [Fact]
    public void Yaml_MissingFile_ReportsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sfmisc_" + Guid.NewGuid().ToString("N") + ".yml");
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().LoadFile(missing));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Yaml_InvalidLoadMode_ListsAllowedValues()
    {
        const string yaml =
            """
            name: x
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            load: { mode: bogus }
            """;
        var ex = Assert.Throws<FlowValidationException>(() => new YamlFlowLoader().Parse(yaml));
        Assert.Contains("Allowed:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yaml_SourceName_DrivesGeneratedFlowId()
    {
        const string yaml =
            """
            name: orders
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            """;
        var flow = new YamlFlowLoader().Parse(yaml);

        // The loader stamps a system-generated flowId derived from the flow name; it must match FlowIdentity.
        Assert.Equal(FlowIdentity.FromName("orders").ToString(), flow.Source.Options["flowId"]);
    }

    [Fact]
    public void Yaml_LoadModeToken_NormalizesDashesAndUnderscores()
    {
        // truncate_load with an underscore must parse to the same mode as the hyphenated truncate-load.
        const string yaml =
            """
            name: x
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            load: { mode: truncate_load }
            """;
        var flow = new YamlFlowLoader().Parse(yaml);
        Assert.Equal(SqlFlow.Core.Model.LoadMode.TruncateLoad, flow.Load.Mode);
    }

    private static string MiscTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfmisc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MiscCleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
