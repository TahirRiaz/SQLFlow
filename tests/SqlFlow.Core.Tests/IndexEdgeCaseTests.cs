using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Ingestion;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge-case coverage for the Index feature area: the desired-index ScriptDom parser
/// (<see cref="DesiredIndexParser"/>) and the canonical/SCD2 index planner
/// (<see cref="CanonicalIndexPlanner"/>). These are pure in-memory tests (no database, no network) and
/// assert behaviors the existing DesiredIndexParserTests, CanonicalIndexPlannerTests, and
/// IndexConsolidationTests do not already cover: multi-column and INCLUDE handling, clustered and unique
/// variants, columnstore being parsed as a different statement kind, batch separators, verbatim text
/// reconstruction, bracket and quoted-identifier escaping, duplicate (non-deduplicated) names, malformed
/// DDL, whitespace handling, empty input, and the planner's escaping/suppression rules.
/// </summary>
public sealed class IndexEdgeCaseTests
{
    private static RelationalObject EdgeTarget(string name = "Fact", string schema = "dbo", string database = "db")
        => new() { Database = database, Schema = schema, Name = name };

    private static ParsedIndex EdgeParseSingle(string script) => Assert.Single(DesiredIndexParser.Parse(script));

    // ----- DesiredIndexParser: multi-column, INCLUDE, ordering -----

    [Fact]
    public void Parse_MultiColumnKey_PreservesAllColumnsAndOrderInText()
    {
        var index = EdgeParseSingle("CREATE NONCLUSTERED INDEX IX_Multi ON dbo.Orders (A, B, C);");

        Assert.Equal("IX_Multi", index.Name);
        // The column list is reconstructed verbatim, columns in author order.
        Assert.Contains("(A, B, C)", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IncludeColumns_AreRetainedAfterKeyColumns()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_Inc ON dbo.Orders (A, B) INCLUDE (C, D);");

        var keyPos = index.StatementText.IndexOf("(A, B)", StringComparison.Ordinal);
        var includePos = index.StatementText.IndexOf("INCLUDE (C, D)", StringComparison.Ordinal);
        Assert.True(keyPos >= 0, "key column list should be present");
        Assert.True(includePos > keyPos, "INCLUDE list should follow the key columns");
    }

    [Fact]
    public void Parse_ColumnSortDirections_ArePreservedVerbatim()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_Dir ON dbo.Orders (A DESC, B ASC);");

        Assert.Contains("A DESC", index.StatementText, StringComparison.Ordinal);
        Assert.Contains("B ASC", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithIndexOptions_PreservesOptionClauseVerbatim()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_Opt ON dbo.Orders (A) WITH (FILLFACTOR = 80, ONLINE = ON);");

        Assert.Contains("WITH (FILLFACTOR = 80, ONLINE = ON)", index.StatementText, StringComparison.Ordinal);
    }

    // ----- DesiredIndexParser: index kinds (clustered, unique, columnstore) -----

    [Theory]
    [InlineData("CREATE CLUSTERED INDEX IX_C ON dbo.T (A);", "IX_C")]
    [InlineData("CREATE UNIQUE INDEX UX ON dbo.T (A);", "UX")]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX UCX ON dbo.T (A);", "UCX")]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX UNX ON dbo.T (A);", "UNX")]
    public void Parse_RowstoreIndexKinds_AreRecognized(string script, string expectedName)
        => Assert.Equal(expectedName, EdgeParseSingle(script).Name);

    [Theory]
    [InlineData("CREATE NONCLUSTERED COLUMNSTORE INDEX IX_CS ON dbo.T (A, B);")]
    [InlineData("CREATE CLUSTERED COLUMNSTORE INDEX CCS ON dbo.T;")]
    public void Parse_ColumnstoreIndexes_AreIgnored_BecauseTheyAreADifferentStatementKind(string script)
    {
        // ScriptDom models CREATE COLUMNSTORE INDEX as CreateColumnStoreIndexStatement, which the
        // collector (it only visits CreateIndexStatement) never sees, so columnstore indexes in a
        // desired-index script are silently dropped rather than created.
        Assert.Empty(DesiredIndexParser.Parse(script));
    }

    // ----- DesiredIndexParser: name / schema / table resolution -----

    [Fact]
    public void Parse_NoSchemaOnTable_LeavesSchemaNull()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_NS ON Orders (A);");

        Assert.Null(index.Schema);
        Assert.Equal("Orders", index.Table);
    }

    [Fact]
    public void Parse_QuotedIdentifierNames_AreUnquoted()
    {
        // The parser is constructed with QuotedIdentifier on, so "double quoted" names are identifiers.
        var index = EdgeParseSingle("CREATE INDEX \"IX Q\" ON dbo.\"My Tbl\" (A);");

        Assert.Equal("IX Q", index.Name);
        Assert.Equal("My Tbl", index.Table);
    }

    [Fact]
    public void Parse_BracketedNameWithEscapedBracket_IsUnescaped()
    {
        // [IX]]X] is the bracket-quoted identifier IX]X (the inner ']' doubled per T-SQL escaping).
        var index = EdgeParseSingle("CREATE INDEX [IX]]X] ON dbo.[Tbl]]X] (A);");

        Assert.Equal("IX]X", index.Name);
        Assert.Equal("Tbl]X", index.Table);
    }

    [Fact]
    public void Parse_LowercaseKeywords_PreserveOriginalIdentifierCasing()
    {
        var index = EdgeParseSingle("create index ix_low on dbo.t (a);");

        Assert.Equal("ix_low", index.Name);
        Assert.Equal("t", index.Table);
        Assert.Equal("dbo", index.Schema);
    }

    // ----- DesiredIndexParser: text reconstruction fidelity -----

    [Fact]
    public void Parse_MultiLineStatement_PreservesNewlinesInStatementText()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_ML ON dbo.T\n(\n  A,\n  B\n);");

        Assert.Contains('\n', index.StatementText);
        Assert.Contains("A,", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LeadingWhitespaceBeforeCreate_IsTrimmedFromStatementText()
    {
        var index = EdgeParseSingle("    CREATE INDEX IX_Ind ON dbo.T (A);");

        Assert.StartsWith("CREATE", index.StatementText, StringComparison.Ordinal);
        Assert.DoesNotContain("  CREATE", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TrailingLineComment_IsNotPartOfStatementText()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_C2 ON dbo.T (A); -- trailing note");

        Assert.DoesNotContain("trailing note", index.StatementText, StringComparison.Ordinal);
        Assert.EndsWith(";", index.StatementText, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StatementWithoutTrailingSemicolon_HasNoSemicolonInText()
    {
        var index = EdgeParseSingle("CREATE INDEX IX_NoSemi ON dbo.T (A)");

        Assert.EndsWith(")", index.StatementText, StringComparison.Ordinal);
    }

    // ----- DesiredIndexParser: multi-statement scripts and separators -----

    [Fact]
    public void Parse_GoBatchSeparators_AreNotEmittedAsIndexes()
    {
        var script = "CREATE INDEX IX_A ON dbo.T (A);\nGO\nCREATE INDEX IX_B ON dbo.T (B);\nGO";

        var names = DesiredIndexParser.Parse(script).Select(i => i.Name).ToList();

        Assert.Equal(new[] { "IX_A", "IX_B" }, names);
        Assert.DoesNotContain(DesiredIndexParser.Parse(script), i => i.StatementText.Contains("GO", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MixedNonIndexStatements_KeepsOnlyCreateIndex()
    {
        var script = """
            CREATE TABLE dbo.T (A int);
            ALTER TABLE dbo.T ADD CONSTRAINT PK_T PRIMARY KEY (A);
            CREATE INDEX IX_Keep ON dbo.T (A);
            DROP INDEX IX_Old ON dbo.T;
            """;

        var index = EdgeParseSingle(script);
        Assert.Equal("IX_Keep", index.Name);
    }

    [Fact]
    public void Parse_DuplicateIndexName_IsNotDeduplicated()
    {
        // The parser is a faithful transcription, not a planner: two CREATE statements with the same
        // index name both come back so the caller (or SQL Server) decides what to do with the conflict.
        var indexes = DesiredIndexParser.Parse(
            "CREATE INDEX IX_Dup ON dbo.T (A);\nCREATE INDEX IX_Dup ON dbo.T (B);");

        Assert.Equal(2, indexes.Count);
        Assert.All(indexes, i => Assert.Equal("IX_Dup", i.Name));
        Assert.NotEqual(indexes[0].StatementText, indexes[1].StatementText);
    }

    [Theory]
    [InlineData(";;;")]
    [InlineData("-- only a comment")]
    [InlineData("/* block comment only */")]
    [InlineData("\t\r\n   ")]
    public void Parse_NoCreateIndexInScript_ReturnsEmpty(string script)
        => Assert.Empty(DesiredIndexParser.Parse(script));

    // ----- DesiredIndexParser: malformed / invalid input -----

    [Theory]
    [InlineData("CREATE INDEX IX ON dbo.T ();")]
    [InlineData("CREATE INDEX IX ON dbo.T INCLUDE (A);")]
    [InlineData("CREATE INDEX IX4 ON srv.db.sch.Tbl (A);")]
    [InlineData("CREATE INDEX IX_A ON dbo.T (A); THIS IS GARBAGE")]
    public void Parse_InvalidScript_ThrowsSqlFlowExceptionMentioningDesiredIndex(string script)
    {
        var error = Assert.Throws<SqlFlowException>(() => DesiredIndexParser.Parse(script));
        Assert.Contains("desired-index", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NullScript_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => DesiredIndexParser.Parse(null!));

    // ----- CanonicalIndexPlanner.Plan: key column handling -----

    [Fact]
    public void Plan_MultiColumnKey_EmitsOneUniqueIndexWithAllColumnsBracketed()
    {
        var statement = Assert.Single(
            CanonicalIndexPlanner.Plan(EdgeTarget(), ["A", "B", "C"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

        Assert.Contains("UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", statement, StringComparison.Ordinal);
        Assert.Contains("([A], [B], [C])", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_KeyColumnCasing_IsPreservedExactly()
    {
        var statement = Assert.Single(
            CanonicalIndexPlanner.Plan(EdgeTarget(), ["lowerId", "MixedCol"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

        // The planner does not normalize casing; it brackets the caller-supplied (already cleaned) names.
        Assert.Contains("([lowerId], [MixedCol])", statement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_BlankDateColumn_OmitsDateIndex(string? dateColumn)
    {
        var statements = CanonicalIndexPlanner.Plan(EdgeTarget(), ["Id"], dateColumn, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false);

        Assert.DoesNotContain(statements, s => s.Contains("[NCI_DateColumn]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_BlankDataSetColumn_OmitsDataSetIndex(string? dataSetColumn)
    {
        var statements = CanonicalIndexPlanner.Plan(EdgeTarget(), ["Id"], null, dataSetColumn, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false);

        Assert.DoesNotContain(statements, s => s.Contains("[NCI_DataSetColumn]", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_EveryGuardedStatement_HasExistenceCheck()
    {
        var statements = CanonicalIndexPlanner.Plan(EdgeTarget(), ["Id"], "OrderDate", "Batch", hasUpdatedDateColumn: true, columnStore: false, hasIdentityPrimaryKey: false);

        Assert.Equal(4, statements.Count);
        Assert.All(statements, s => Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.indexes", s, StringComparison.Ordinal));
    }

    // ----- CanonicalIndexPlanner.Plan: columnstore / scd2 interactions -----

    [Fact]
    public void Plan_Scd2WithColumnstore_StillEmitsColumnstoreButNoKeyIndex()
    {
        var statements = CanonicalIndexPlanner.Plan(EdgeTarget(), ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: true, hasIdentityPrimaryKey: false, scd2Enabled: true);

        Assert.Contains(statements, s => s.Contains("CLUSTERED COLUMNSTORE INDEX [CCSI_Fact]", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => s.Contains("[NCI_KeyColumn]", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_ColumnstoreName_IsSanitizedButTargetReferenceKeepsRawName()
    {
        var statements = CanonicalIndexPlanner.Plan(EdgeTarget("My Fact!"), [], null, null, hasUpdatedDateColumn: false, columnStore: true, hasIdentityPrimaryKey: false);

        var statement = Assert.Single(statements);
        // Non-alphanumeric (and non-underscore) characters are stripped from the index name only.
        Assert.Contains("[CCSI_MyFact]", statement, StringComparison.Ordinal);
        Assert.Contains("ON [dbo].[My Fact!]", statement, StringComparison.Ordinal);
    }

    // ----- CanonicalIndexPlanner.Plan: identifier escaping -----

    [Fact]
    public void Plan_TargetNameWithRightBracket_IsEscapedInBothLiteralAndBracketedForms()
    {
        var statement = Assert.Single(
            CanonicalIndexPlanner.Plan(EdgeTarget("Fa]ct"), ["I]d"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

        // The bracketed object reference doubles ']' to ']]'.
        Assert.Contains("[dbo].[Fa]]ct]", statement, StringComparison.Ordinal);
        // The OBJECT_ID literal uses the same escaped, bracketed text inside an N'...' literal.
        Assert.Contains("OBJECT_ID(N'[dbo].[Fa]]ct]')", statement, StringComparison.Ordinal);
        // The key column is bracketed and escaped too.
        Assert.Contains("([I]]d])", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_SchemaWithSingleQuote_IsQuoteEscapedInObjectIdLiteral()
    {
        var statement = Assert.Single(
            CanonicalIndexPlanner.Plan(EdgeTarget(schema: "dbo's"), ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

        // A single quote in the qualified name is doubled inside the N'...' literal so it stays valid T-SQL.
        Assert.Contains("OBJECT_ID(N'[dbo''s].[Fact]')", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => CanonicalIndexPlanner.Plan(null!, ["Id"], null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

    [Fact]
    public void Plan_NullKeyColumns_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => CanonicalIndexPlanner.Plan(EdgeTarget(), null!, null, null, hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false));

    // ----- CanonicalIndexPlanner.Scd2KeyIndexStatements -----

    [Fact]
    public void Scd2KeyIndexStatements_AlwaysReturnExactlyThreeStatements()
    {
        // The migration is a fixed three-step sequence regardless of key shape: targeted drop, dynamic
        // drop of any whole-table-unique key index, then the guarded filtered-unique create.
        var single = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["Id"], "IsCurrent");
        var composite = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["A", "B", "C"], "IsCurrent");

        Assert.Equal(3, single.Count);
        Assert.Equal(3, composite.Count);
    }

    [Fact]
    public void Scd2KeyIndexStatements_TargetedDrop_OnlyFiresForAWrongShapedSameNamedIndex()
    {
        // The first statement drops NCI_KeyColumn only when it is NOT already the correct filtered-unique
        // form, so a table that already has the right index is left untouched by this step.
        var dropTargeted = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["Id"], "IsCurrent")[0];

        Assert.Contains("IF EXISTS (SELECT 1 FROM sys.indexes", dropTargeted, StringComparison.Ordinal);
        Assert.Contains("name = N'NCI_KeyColumn'", dropTargeted, StringComparison.Ordinal);
        Assert.Contains("NOT (is_unique = 1 AND has_filter = 1)", dropTargeted, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndexStatements_DynamicDrop_LeavesPrimaryKeysAndUniqueConstraintsAlone()
    {
        // The dynamic drop only removes plain unique nonclustered INDEXES on the business key; a primary key
        // or a unique CONSTRAINT must be preserved (dropping them is unsafe and signals a modeling conflict).
        var dynamicDrop = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["Id"], "IsCurrent")[1];

        Assert.Contains("i.is_primary_key = 0 AND i.is_unique_constraint = 0", dynamicDrop, StringComparison.Ordinal);
        // It targets only rowstore nonclustered (type 2), unique, non-filtered indexes.
        Assert.Contains("i.type = 2 AND i.is_unique = 1 AND i.has_filter = 0", dynamicDrop, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndexStatements_Create_IsGuardedSoReapplyIsANoOp()
    {
        // The final create is wrapped in IF NOT EXISTS, so running the migration twice does not error on the
        // second pass (the index is created only when absent).
        var create = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["Id"], "IsCurrent")[2];

        Assert.StartsWith("IF NOT EXISTS (SELECT 1 FROM sys.indexes", create, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", create, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndexStatements_NullFlagColumn_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), ["Id"], null!));

    [Fact]
    public void Scd2KeyIndexStatements_NullTarget_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => CanonicalIndexPlanner.Scd2KeyIndexStatements(null!, ["Id"], "Flag"));

    [Fact]
    public void Scd2KeyIndexStatements_NullKeyColumns_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget(), null!, "Flag"));
}
