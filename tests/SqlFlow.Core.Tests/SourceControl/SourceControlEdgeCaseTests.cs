using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.SourceControl;
using SqlFlow.SourceControl;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// Non-overlapping edge cases for the source-control feature's pure logic: the flowType: scm YAML loader
/// (validation, connection resolution, data-table normalization, include/exclude filters, defaults) and the
/// snapshot writer's on-disk contract (file naming, deletion sweep, path-rooting, idempotency, byte equality).
/// Every test is in-memory and deterministic; nothing here touches SQL Server, git, or wall-clock time. These
/// complement <see cref="SourceControlLoaderTests"/> and <see cref="SnapshotWriterTests"/> without repeating a
/// case either already proves.
/// </summary>
public sealed class SourceControlEdgeCaseTests : IDisposable
{
    private readonly string _scmEdgeDir =
        Path.Combine(Path.GetTempPath(), "sqlflow_scm_edge_" + Guid.NewGuid().ToString("N"));

    public SourceControlEdgeCaseTests() => Directory.CreateDirectory(_scmEdgeDir);

    private static YamlSourceControlFlowLoader EdgeLoader() => new();

    /// <summary>A minimal but valid scm document with an optional extra body appended verbatim. The base
    /// declares a bare SQL Server connection and a local-only repository, so any single block under test can be
    /// added without dragging in unrelated validation.</summary>
    private static string EdgeDocument(string extra = "") => $"""
        flowType: scm
        name: edge-flow
        connections:
          DW:
        source:
          server: DW
        repository:
          path: ./repo
        {extra}
        """;

    /// <summary>A valid scm document whose <c>repository:</c> block is exactly <paramref name="repositoryLines"/>
    /// (each already indented two spaces, the child level under <c>repository:</c>). Lets a repository edge case
    /// state only the keys it exercises while the rest of the document stays valid.</summary>
    private static string RepoDocument(string repositoryLines) => $"""
        flowType: scm
        name: edge-flow
        connections:
          DW:
        source:
          server: DW
        repository:
        {repositoryLines}
        """;

    private static ScriptedDatabase EdgeDb(
        string name, params (string Folder, string Schema, string ObjectName, string Sql)[] objects)
        => new()
        {
            DatabaseName = name,
            Objects = objects.Select(o => new ScriptedObject
            {
                Folder = o.Folder,
                Schema = o.Schema,
                Name = o.ObjectName,
                RelativePath = $"{name}/{o.Folder}/{o.Schema}.{o.ObjectName}.sql",
                Sql = o.Sql,
            }).ToList(),
        };

    private string EdgePath(params string[] parts) => Path.Combine([_scmEdgeDir, .. parts]);

    // --- Loader: empty and malformed documents -------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("# only a comment\n")]
    [InlineData("null")]
    public void Parse_EmptyOrCommentOnlyDocument_IsReportedAsEmpty(string yaml)
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(yaml));
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MalformedYaml_IsWrappedAsInvalidYaml()
    {
        // A block sequence indented under a mapping value with a stray scalar is a grammar error.
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("name: x\n  - broken: ["));
        Assert.Contains("invalid YAML", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_PropagatesTheSourceLabel_IntoValidationMessages()
    {
        // The caller-supplied source label must prefix the error so a multi-file load points at the right file.
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("", "warehouse.scm.yaml"));
        Assert.StartsWith("warehouse.scm.yaml:", ex.Message, StringComparison.Ordinal);
    }

    // --- Loader: required fields ---------------------------------------------------------------

    [Theory]
    [InlineData("name:")]
    [InlineData("name: \"   \"")]
    public void Parse_BlankOrMissingName_IsRejected(string nameLine)
    {
        var yaml = $"""
            flowType: scm
            {nameLine}
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./repo
            """;
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(yaml));
        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingSource_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            repository:
              path: ./repo
            """));
        Assert.Contains("'source' is required", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("path:")]
    [InlineData("path: \"  \"")]
    public void Parse_RepositoryWithoutUsablePath_IsRejected(string pathLine)
    {
        var yaml = $"""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            source:
              server: DW
            repository:
              {pathLine}
            """;
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(yaml));
        Assert.Contains("repository.path", ex.Message, StringComparison.Ordinal);
    }

    // --- Loader: source endpoint resolution ----------------------------------------------------

    [Fact]
    public void Parse_SourceWithBothServerAndConnection_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            source:
              server: DW
              connection: ${env:DW_CS}
            repository:
              path: ./repo
            """));
        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SourceWithNeitherServerNorConnection_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            source:
              database: Warehouse
            repository:
              path: ./repo
            """));
        Assert.Contains("needs a connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SourceServer_ReferencingUndeclaredConnection_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            source:
              server: NOPE
            repository:
              path: ./repo
            """));
        Assert.Contains("not declared", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DirectConnectionOnSource_SynthesizesTheSourceConnection()
    {
        // No connections block at all: a direct connection: on source registers under the synthetic name
        // "source" and the flow addresses it as @source.
        var doc = EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            source:
              connection: Server=(local);Database=Warehouse;Integrated Security=true;TrustServerCertificate=true
            repository:
              path: ./repo
            """);

        Assert.Equal("source", doc.Flow.Server);
        Assert.Equal("@source", doc.Flow.ConnectionReference);
        Assert.Single(doc.Connections);
        Assert.Equal("source", doc.Connections[0].Alias);
    }

    [Fact]
    public void Parse_BareConnection_ResolvesTheConventionalEnvironmentReference()
    {
        // A bare alias must resolve ${env:SQLFLOW_CONN_<NAME>} so a versioned document carries no secret.
        var doc = EdgeLoader().Parse(EdgeDocument());

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("DW", connection.Alias);
        Assert.Equal("${env:SQLFLOW_CONN_DW}", connection.ConnectionRef);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("name!")]
    [InlineData("a/b")]
    public void Parse_InvalidConnectionName_IsRejected(string connectionName)
    {
        var yaml = $"""
            flowType: scm
            name: edge-flow
            connections:
              "{connectionName}":
            source:
              server: "{connectionName}"
            repository:
              path: ./repo
            """;
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(yaml));
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnknownProviderOnConnection_IsRejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
                provider: db2
                connection: ${env:DW}
            source:
              server: DW
            repository:
              path: ./repo
            """));
        Assert.Contains("unknown provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AzdbProviderSource_IsAccepted()
    {
        // azdb counts as SQL Server for the source guard; this proves the guard does not reject Azure SQL.
        var doc = EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
                provider: azdb
                connection: ${env:DW}
            source:
              server: DW
            repository:
              path: ./repo
            """);

        Assert.Equal("DW", doc.Flow.Server);
    }

    // --- Loader: secret-less contract for git credentials --------------------------------------

    [Theory]
    [InlineData("hunter2")]            // a bare literal
    [InlineData("${env:X")]            // missing the closing brace
    [InlineData("prefix${env:X}")]     // reference not the whole value
    [InlineData("${env:X}suffix")]     // trailing text after the reference
    public void Parse_RepositoryUsernameThatIsNotAWholeReference_IsRejected(string username)
    {
        var yaml = RepoDocument($$"""
              path: ./repo
              remote: https://example.invalid/r.git
              secret: ${env:S}
              username: "{{username}}"
            """);

        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(yaml));
        Assert.Contains("repository.username", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WhitespacePaddedReference_IsAcceptedAsAReference()
    {
        // The literal guard trims first, so a padded ${...} reference is still a reference (not a literal).
        var doc = EdgeLoader().Parse(RepoDocument("""
              path: ./repo
              remote: https://example.invalid/r.git
              secret: "   ${env:S}   "
            """));

        Assert.Equal("   ${env:S}   ", doc.Flow.Repository.Secret);
    }

    [Fact]
    public void Parse_RemoteWithSecretButNoUsername_IsAccepted()
    {
        // Pushing requires a secret, not necessarily a username; a token-only credential is valid.
        var doc = EdgeLoader().Parse(RepoDocument("""
              path: ./repo
              remote: https://example.invalid/r.git
              secret: ${env:S}
            """));

        Assert.Equal("https://example.invalid/r.git", doc.Flow.Repository.Remote);
        Assert.Equal("${env:S}", doc.Flow.Repository.Secret);
        Assert.Null(doc.Flow.Repository.Username);
    }

    // --- Loader: defaults ----------------------------------------------------------------------

    [Fact]
    public void Parse_AuthorEmail_DefaultsToLocalhost_WhenAbsent()
    {
        // The existing minimal-defaults test asserts the author NAME default; the email default is distinct.
        var doc = EdgeLoader().Parse(EdgeDocument());

        Assert.Equal("sqlflow@localhost", doc.Flow.Repository.AuthorEmail);
    }

    [Fact]
    public void Parse_AuthorNameSet_ButBlankEmail_FallsBackToDefaultEmail()
    {
        var doc = EdgeLoader().Parse(RepoDocument("""
              path: ./repo
              author:
                name: Release Bot
                email: "   "
            """));

        Assert.Equal("Release Bot", doc.Flow.Repository.AuthorName);
        Assert.Equal("sqlflow@localhost", doc.Flow.Repository.AuthorEmail);
    }

    [Fact]
    public void Parse_ExplicitBranch_IsHonored_OverTheMainDefault()
    {
        var doc = EdgeLoader().Parse(RepoDocument("""
              path: ./repo
              branch: release/2025
            """));

        Assert.Equal("release/2025", doc.Flow.Repository.Branch);
    }

    [Fact]
    public void Parse_DescriptionBlank_NormalizesToNull()
    {
        var doc = EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            description: "   "
            connections:
              DW:
            source:
              server: DW
            repository:
              path: ./repo
            """);

        Assert.Null(doc.Flow.Description);
    }

    [Fact]
    public void Parse_StableFlowId_IsPositive_AndDeterministicForAName()
    {
        var first = EdgeLoader().Parse(EdgeDocument()).Flow.FlowId;
        var second = EdgeLoader().Parse(EdgeDocument()).Flow.FlowId;

        Assert.Equal(first, second);
        Assert.True(first > 0, "the name-derived flow id must be a positive int");
    }

    [Fact]
    public void Parse_UnknownTopLevelKeys_AreIgnored()
    {
        // The deserializer ignores unmatched properties, so a forward-compatible extra key must not throw.
        var doc = EdgeLoader().Parse(EdgeDocument("""
            futureFeature: enabled
            anotherUnknown:
              nested: 1
            """));

        Assert.Equal("edge-flow", doc.Flow.SysAlias);
    }

    // --- Loader: include / exclude object-type filters -----------------------------------------

    [Theory]
    [InlineData("table")]
    [InlineData("TABLE")]
    [InlineData("StOrEdPrOcEdUrE")]
    public void Parse_IncludeType_IsMatchedCaseInsensitively(string typeName)
    {
        var doc = EdgeLoader().Parse(EdgeDocument($"""
            scripting:
              include:
                - {typeName}
            """));

        // The entry is preserved verbatim (case as written); only the membership check is case-insensitive.
        var included = Assert.Single(doc.Flow.Scripting.IncludeTypes);
        Assert.Equal(typeName, included);
    }

    [Fact]
    public void Parse_UnknownExcludeType_IsRejected_AndNamesTheField()
    {
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(EdgeDocument("""
            scripting:
              exclude:
                - Galaxy
            """)));
        Assert.Contains("scripting.exclude", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Galaxy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BlankIncludeEntries_AreDropped_KeepingTheValidOnes()
    {
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              include:
                - Table
                - "   "
                - View
            """));

        Assert.Equal(["Table", "View"], doc.Flow.Scripting.IncludeTypes);
    }

    [Fact]
    public void Parse_IncludeAndExclude_CanBothBePopulated()
    {
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              include:
                - Table
                - View
              exclude:
                - SecurityPolicy
            """));

        Assert.Equal(["Table", "View"], doc.Flow.Scripting.IncludeTypes);
        Assert.Equal(["SecurityPolicy"], doc.Flow.Scripting.ExcludeTypes);
    }

    // --- Loader: excluded schemas --------------------------------------------------------------

    [Fact]
    public void Parse_WithNoScriptingBlock_ExcludesTheEngineStagingSchema()
    {
        // The staging schema holds per-flow work tables that every run rebuilds and drops, so a snapshot that
        // versioned them would churn on objects that are not part of the database's definition, and a table
        // dropped mid-walk would fail the run.
        var doc = EdgeLoader().Parse(EdgeDocument());

        Assert.Equal([StagingConventions.SchemaName], doc.Flow.Scripting.ExcludeSchemas);
    }

    [Fact]
    public void Parse_ExcludeSchemas_ReplacesTheDefault_AndNormalizesEachName()
    {
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              excludeSchemas:
                - "[work]"
                - "  scratch  "
                - "   "
                - WORK
            """));

        // Brackets stripped, blanks dropped, duplicates removed case-insensitively, and the staging default gone
        // because the flow named its own list.
        Assert.Equal(["work", "scratch"], doc.Flow.Scripting.ExcludeSchemas);
    }

    [Fact]
    public void Parse_EmptyExcludeSchemas_ScriptsEverySchema()
    {
        // An explicitly empty list is the way to ask for the staging schema to be versioned after all.
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              excludeSchemas: []
            """));

        Assert.Empty(doc.Flow.Scripting.ExcludeSchemas);
    }

    // --- Loader: scripting parallelism ---------------------------------------------------------

    [Fact]
    public void Parse_WithNoParallelism_FansOutOverTheDefaultLaneCount()
    {
        // SMO spends dozens of round trips on a single table, so a one-connection walk over a few hundred objects
        // takes many minutes. The default exists so a flow that says nothing still gets a snapshot in reasonable
        // wall-clock; the emitted files do not depend on it either way.
        var doc = EdgeLoader().Parse(EdgeDocument());

        Assert.Equal(SourceControlScripting.DefaultParallelism, doc.Flow.Scripting.Parallelism);
    }

    [Fact]
    public void Parse_Parallelism_IsTakenAsAuthored()
    {
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              parallelism: 1
            """));

        Assert.Equal(1, doc.Flow.Scripting.Parallelism);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(33)]
    public void Parse_ParallelismOutOfRange_Fails(int lanes)
    {
        // Zero or fewer lanes would script nothing at all, and an unbounded count would open as many connections
        // to a production server as the author happened to type.
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(EdgeDocument($"""
            scripting:
              parallelism: {lanes}
            """)));

        Assert.Contains("'scripting.parallelism' must be between 1 and 32", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"but was {lanes}", ex.Message, StringComparison.Ordinal);
    }

    // --- Loader: data-table normalization ------------------------------------------------------

    [Theory]
    [InlineData("Config", "dbo.Config")]                       // unqualified gets the dbo default
    [InlineData("ref.Calendar", "ref.Calendar")]               // already schema-qualified
    [InlineData("Warehouse.ref.Calendar", "ref.Calendar")]     // 3-part keeps the rightmost two
    [InlineData("[ref].[Calendar]", "ref.Calendar")]           // brackets stripped
    [InlineData("[my db].[my schema].[my table]", "my schema.my table")] // bracketed parts with spaces
    [InlineData("  spaced  ", "dbo.spaced")]                    // surrounding whitespace trimmed
    public void Parse_DataTable_NormalizesToCanonicalSchemaDotTable(string input, string expected)
    {
        var doc = EdgeLoader().Parse(EdgeDocument($"""
            scripting:
              data:
                - "{input}"
            """));

        var table = Assert.Single(doc.Flow.Scripting.DataTables);
        Assert.Equal(expected, table);
    }

    [Fact]
    public void Parse_DataTables_AreDeduplicatedCaseInsensitively_AcrossBracketing()
    {
        // dbo.Config, [DBO].[CONFIG], and Config all canonicalize to the same key; only the first survives.
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              data:
                - dbo.Config
                - "[DBO].[CONFIG]"
                - Config
                - ref.Calendar
            """));

        Assert.Equal(["dbo.Config", "ref.Calendar"], doc.Flow.Scripting.DataTables);
    }

    [Fact]
    public void Parse_DataTables_BlankEntriesAreSkipped()
    {
        var doc = EdgeLoader().Parse(EdgeDocument("""
            scripting:
              data:
                - "   "
                - dbo.Config
            """));

        Assert.Equal(["dbo.Config"], doc.Flow.Scripting.DataTables);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("[]")]
    public void Parse_DataTableWithNoNamePart_IsRejected(string input)
    {
        // A value made only of separators or empty brackets has no name component; that is a hard error
        // (distinct from a blank string, which is silently skipped).
        var ex = Assert.Throws<FlowValidationException>(() => EdgeLoader().Parse(EdgeDocument($"""
            scripting:
              data:
                - "{input}"
            """)));
        Assert.Contains("empty table name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DatabaseName_IsCarried_FromSourceDatabase()
    {
        var doc = EdgeLoader().Parse("""
            flowType: scm
            name: edge-flow
            connections:
              DW:
            source:
              server: DW
              database: Warehouse
            repository:
              path: ./repo
            """);

        Assert.Equal("Warehouse", doc.Flow.Database);
    }

    // --- Snapshot writer: argument guards ------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Write_BlankWorkingDirectory_Throws(string workingDirectory)
    {
        Assert.Throws<ArgumentException>(() =>
            SnapshotWriter.Write(workingDirectory, EdgeDb("Warehouse", ("Table", "dbo", "T", "x\n"))));
    }

    [Fact]
    public void Write_NullDatabase_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SnapshotWriter.Write(_scmEdgeDir, null!));
    }

    // --- Snapshot writer: empty selection ------------------------------------------------------

    [Fact]
    public void Write_NoObjects_ProducesAnEmptyResult_AndNoDatabaseFolder()
    {
        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        Assert.Empty(result.Added);
        Assert.Empty(result.Changed);
        Assert.Empty(result.Unchanged);
        Assert.Empty(result.Deleted);
        Assert.Equal(0, result.TotalChanged);
        Assert.False(Directory.Exists(EdgePath("Warehouse")));
    }

    [Fact]
    public void Write_EmptySnapshotOverAPopulatedFolder_DeletesEveryTrackedFile()
    {
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "A", "a\n"),
            ("View", "dbo", "B", "b\n")));

        // The next snapshot sees nothing (an empty database, or a fully filtered selection): every file goes.
        var second = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        Assert.Equal(2, second.Deleted.Count);
        Assert.False(File.Exists(EdgePath("Warehouse", "Table", "dbo.A.sql")));
        Assert.False(File.Exists(EdgePath("Warehouse", "View", "dbo.B.sql")));
    }

    // --- Snapshot writer: counting and ordering ------------------------------------------------

    [Fact]
    public void Write_TotalChanged_CountsAddedChangedAndDeleted_ButNotUnchanged()
    {
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "Stable", "stable\n"),
            ("Table", "dbo", "Mutate", "v1\n"),
            ("Table", "dbo", "Vanish", "gone\n")));

        // Stable is byte-identical, Mutate changes, Vanish disappears, New appears.
        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "Stable", "stable\n"),
            ("Table", "dbo", "Mutate", "v2\n"),
            ("Table", "dbo", "New", "fresh\n")));

        Assert.Single(result.Added);
        Assert.Single(result.Changed);
        Assert.Single(result.Deleted);
        Assert.Single(result.Unchanged);
        Assert.Equal(3, result.TotalChanged);
    }

    [Fact]
    public void Write_DeletedPaths_AreSortedOrdinal_ForADeterministicReport()
    {
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "Zebra", "z\n"),
            ("Table", "dbo", "Apple", "a\n"),
            ("Table", "dbo", "Mango", "m\n")));

        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        Assert.Equal(
            [
                "Warehouse/Table/dbo.Apple.sql",
                "Warehouse/Table/dbo.Mango.sql",
                "Warehouse/Table/dbo.Zebra.sql",
            ],
            result.Deleted);
    }

    [Fact]
    public void Write_DeletedPaths_AreForwardSlashed_RegardlessOfPlatformSeparator()
    {
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "Orders", "o\n")));

        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        var deleted = Assert.Single(result.Deleted);
        Assert.DoesNotContain('\\', deleted);
        Assert.Equal("Warehouse/Table/dbo.Orders.sql", deleted);
    }

    // --- Snapshot writer: deletion sweep is scoped --------------------------------------------

    [Fact]
    public void Write_LeavesNonSqlFilesUntouched_WhenSweepingDeletedObjects()
    {
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "Orders", "o\n")));

        // A hand-authored README inside the database folder is not a scripted .sql file and must survive a
        // sweep that removes the now-dropped Orders table.
        var readme = EdgePath("Warehouse", "Table", "README.md");
        File.WriteAllText(readme, "notes\n");

        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        Assert.Equal("Warehouse/Table/dbo.Orders.sql", Assert.Single(result.Deleted));
        Assert.True(File.Exists(readme));
    }

    [Fact]
    public void Write_SweepsNestedSubfolders_ForDroppedObjects()
    {
        // A scripted object can live several folders deep; the recursive sweep must still collect it.
        var deepDb = new ScriptedDatabase
        {
            DatabaseName = "Warehouse",
            Objects =
            [
                new ScriptedObject
                {
                    Folder = "StoredProcedure", Schema = "billing", Name = "Run",
                    RelativePath = "Warehouse/StoredProcedure/sub/billing.Run.sql", Sql = "go\n",
                },
            ],
        };
        SnapshotWriter.Write(_scmEdgeDir, deepDb);
        Assert.True(File.Exists(EdgePath("Warehouse", "StoredProcedure", "sub", "billing.Run.sql")));

        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse"));

        Assert.Equal("Warehouse/StoredProcedure/sub/billing.Run.sql", Assert.Single(result.Deleted));
    }

    // --- Snapshot writer: path rooting ---------------------------------------------------------

    [Fact]
    public void Write_RelativePathThatResolvesBackInsideRoot_IsAllowed()
    {
        // A path with .. segments that still normalizes to a location inside the working tree is legitimate;
        // the guard rejects escapes, not every appearance of "..".
        var db = new ScriptedDatabase
        {
            DatabaseName = "Warehouse",
            Objects =
            [
                new ScriptedObject
                {
                    Folder = "Table", Schema = "dbo", Name = "Customer",
                    RelativePath = "Warehouse/../Warehouse/Table/dbo.Customer.sql", Sql = "ok\n",
                },
            ],
        };

        var result = SnapshotWriter.Write(_scmEdgeDir, db);

        Assert.Single(result.Added);
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", "dbo.Customer.sql")));
    }

    [Fact]
    public void Write_AbsoluteEscapeRelativePath_IsRefused()
    {
        // An absolute path under Path.Combine wins outright and lands outside the root; it must be refused.
        var escape = Path.Combine(Path.GetTempPath(), "sqlflow_scm_escape_" + Guid.NewGuid().ToString("N") + ".sql");
        var db = new ScriptedDatabase
        {
            DatabaseName = "Warehouse",
            Objects =
            [
                new ScriptedObject
                {
                    Folder = "Table", Schema = "dbo", Name = "evil",
                    RelativePath = escape, Sql = "x\n",
                },
            ],
        };

        Assert.Throws<SqlFlowException>(() => SnapshotWriter.Write(_scmEdgeDir, db));
        Assert.False(File.Exists(escape));
    }

    // --- Snapshot writer: content fidelity -----------------------------------------------------

    [Fact]
    public void Write_PreservesObjectScriptBytesExactly()
    {
        // The writer must not re-encode or reflow the generated script; it is the committed artifact verbatim.
        const string sql = "CREATE TABLE [dbo].[T] (\n    Id int NOT NULL,\n    Name nvarchar(50) NULL\n);\n";
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "T", sql)));

        Assert.Equal(sql, File.ReadAllText(EdgePath("Warehouse", "Table", "dbo.T.sql")));
    }

    [Fact]
    public void Write_CrlfVersusLf_CountsAsAChange()
    {
        // Content equality is byte-ordinal, so a line-ending flip is a real change, not a no-op.
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "T", "line\n")));

        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "T", "line\r\n")));

        Assert.Single(result.Changed);
        Assert.Empty(result.Unchanged);
        Assert.Equal("line\r\n", File.ReadAllText(EdgePath("Warehouse", "Table", "dbo.T.sql")));
    }

    [Fact]
    public void Write_RemainsUnchanged_AcrossThreeIdenticalRuns()
    {
        var db = EdgeDb("Warehouse", ("Table", "dbo", "T", "stable\n"));
        SnapshotWriter.Write(_scmEdgeDir, db);
        SnapshotWriter.Write(_scmEdgeDir, db);

        var third = SnapshotWriter.Write(_scmEdgeDir, db);

        Assert.Empty(third.Added);
        Assert.Empty(third.Changed);
        Assert.Single(third.Unchanged);
    }

    // --- Snapshot writer: special characters and layout ---------------------------------------

    [Theory]
    [InlineData("dbo", "Order Details")]      // a space in the object name
    [InlineData("ref", "Kalender_ÆØÅ")]        // Nordic letters the cleanup keeps
    [InlineData("dbo", "Tab+le")]              // a punctuation character that is path-legal
    public void Write_SpecialCharactersInObjectName_RoundTripOnDisk(string schema, string objectName)
    {
        var fileName = $"{schema}.{objectName}.sql";
        var db = new ScriptedDatabase
        {
            DatabaseName = "Warehouse",
            Objects =
            [
                new ScriptedObject
                {
                    Folder = "Table", Schema = schema, Name = objectName,
                    RelativePath = $"Warehouse/Table/{fileName}", Sql = "x\n",
                },
            ],
        };

        var result = SnapshotWriter.Write(_scmEdgeDir, db);

        Assert.Single(result.Added);
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", fileName)));
    }

    [Fact]
    public void Write_SameNameDifferentSchema_ProducesTwoFiles()
    {
        // Two objects share a name across schemas; the schema-qualified file name keeps them distinct.
        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "Account", "a\n"),
            ("Table", "audit", "Account", "b\n")));

        Assert.Equal(2, result.Added.Count);
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", "dbo.Account.sql")));
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", "audit.Account.sql")));
    }

    [Fact]
    public void Write_TwoDatabaseFolders_AreIndependent_InOneWorkingTree()
    {
        // Snapshotting Warehouse must not delete a sibling database's tracked files in the same repository,
        // and a later snapshot of Sales must not disturb Warehouse.
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "Customer", "c\n")));
        SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Sales", ("Table", "dbo", "Invoice", "i\n")));

        var rerun = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse", ("Table", "dbo", "Customer", "c\n")));

        Assert.Empty(rerun.Deleted);
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", "dbo.Customer.sql")));
        Assert.True(File.Exists(EdgePath("Sales", "Table", "dbo.Invoice.sql")));
    }

    [Fact]
    public void Write_DataFolder_LivesBesideTheTableFolder()
    {
        // The Data folder (scripted rows) is kept distinct from the schema-only Table folder for the same table.
        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "Calendar", "CREATE TABLE [dbo].[Calendar] (...);\n"),
            ("Data", "dbo", "Calendar", "INSERT INTO [dbo].[Calendar] VALUES (1);\n")));

        Assert.Equal(2, result.Added.Count);
        Assert.True(File.Exists(EdgePath("Warehouse", "Table", "dbo.Calendar.sql")));
        Assert.True(File.Exists(EdgePath("Warehouse", "Data", "dbo.Calendar.sql")));
    }

    [Fact]
    public void Write_RelativeWorkingDirectory_IsRootedToAnAbsolutePath()
    {
        // A relative working directory must be honored against the process current directory, the same way the
        // CLI hands a document-relative repository path to the writer.
        var previous = Directory.GetCurrentDirectory();
        var relativeName = "sqlflow_scm_rel_" + Guid.NewGuid().ToString("N");
        Directory.SetCurrentDirectory(_scmEdgeDir);
        try
        {
            var result = SnapshotWriter.Write(relativeName, EdgeDb("Warehouse", ("Table", "dbo", "T", "x\n")));

            Assert.Single(result.Added);
            Assert.True(File.Exists(EdgePath(relativeName, "Warehouse", "Table", "dbo.T.sql")));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void TotalChanged_FormatsConsistently_UnderInvariantCulture()
    {
        // The run product's counts are surfaced in artifacts; rendering them must not depend on machine culture.
        var result = SnapshotWriter.Write(_scmEdgeDir, EdgeDb("Warehouse",
            ("Table", "dbo", "A", "a\n"),
            ("View", "dbo", "B", "b\n")));

        Assert.Equal("2", result.TotalChanged.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (Directory.Exists(_scmEdgeDir))
        {
            Directory.Delete(_scmEdgeDir, recursive: true);
        }
    }
}
