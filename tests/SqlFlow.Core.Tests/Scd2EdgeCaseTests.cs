using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Schema;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge cases for the application-managed SCD Type 2 feature: the upsert text generator
/// (backfill, close, insert), the filtered-unique current-key index planner, and the YAML versioning.scd2
/// surface. These probe boundaries the headline tests do not: system-column on/off permutations, composite
/// business keys, ExcludeFromChecksum interaction with explicit tracked columns, bracket escaping in
/// identifiers, the open and epoch sentinels, the as-of/required-column guards, that SCD2 ignores the plain
/// upsert flags, and the loader's distinct-column and tracked-column-cleaning rules. Every case is pure text
/// generation or parsing, so it runs without a database and is fully deterministic (all inputs are fixed).
/// </summary>
public sealed class Scd2EdgeCaseTests
{
    private const string EdgeAsOf = "2026-06-16 11:22:33.444";
    private const string EdgeOpen = "9999-12-31 23:59:59.999";
    private const string EdgeEpoch = "1900-01-01 00:00:00.000";

    private static readonly RelationalObject EdgeTarget = new() { Database = "DW", Schema = "dim", Name = "Customer" };
    private static readonly RelationalObject EdgeStaging = new() { Database = "DW", Schema = "dim", Name = "stg_Customer" };

    // A fully wired SCD2 options instance; individual tests override single facets with a `with` expression so
    // each case isolates one variable. Mirrors the shape the production runner builds.
    private static UpsertOptions EdgeOptions() => new()
    {
        DataColumns = ["CustomerId", "Name", "City"],
        KeyColumns = ["CustomerId"],
        Scd2Enabled = true,
        Scd2ValidFromColumn = "ValidFrom_DW",
        Scd2ValidToColumn = "ValidTo_DW",
        Scd2CurrentFlagColumn = "IsCurrent_DW",
        Scd2TrackedColumns = [],
        Scd2AsOfLiteral = EdgeAsOf,
        InsertedDateColumn = "InsertedDate_DW",
        UpdatedDateColumn = "UpdatedDate_DW",
        RowStatusColumn = "RowStatus_DW",
    };

    private static IReadOnlyList<UpsertStatement> EdgeGen(UpsertOptions options)
        => UpsertGenerator.GenerateStatements(EdgeTarget, EdgeStaging, options);

    // Index into the three SCD2 statements by intent, so an assertion does not silently read the wrong one if
    // the close step is elided (key-only dimensions emit only backfill + insert).
    private static string Backfill(IReadOnlyList<UpsertStatement> s) => s[0].Sql;

    // ---- Backfill (statement 1) -------------------------------------------------------------------------

    [Fact]
    public void Backfill_WithoutInsertedDateColumn_FallsBackToEpochOnly()
    {
        // With no InsertedDate column the effective-from cannot reference it: COALESCE collapses to
        // (ValidFrom, epoch) and the InsertedDate name must not appear in the backfill at all.
        var sql = Backfill(EdgeGen(EdgeOptions() with { InsertedDateColumn = null }));

        Assert.Contains($"COALESCE(trg.[ValidFrom_DW], '{EdgeEpoch}')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("InsertedDate_DW", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Backfill_IsAlwaysEmitted_EvenForAKeyOnlyDimension()
    {
        // A key-only dimension has nothing to version, so the close step is dropped; but pre-existing rows
        // still must be stamped current, so the backfill is unconditional and remains the first statement.
        var statements = EdgeGen(EdgeOptions() with { DataColumns = ["CustomerId"] });

        Assert.Equal(2, statements.Count);
        Assert.Equal(UpsertStatementKind.Update, statements[0].Kind);
        Assert.Contains("WHERE trg.[ValidTo_DW] IS NULL", Backfill(statements), StringComparison.Ordinal);
        Assert.Contains("trg.[IsCurrent_DW] = 1", Backfill(statements), StringComparison.Ordinal);
    }

    [Fact]
    public void Backfill_UsesTheOpenSentinel_NotTheAsOf()
    {
        // A backfilled pre-existing row is the CURRENT version, so its ValidTo is the open sentinel, never the
        // run's as-of (which belongs to closed rows). Guard against a regression that stamps the as-of here.
        var sql = Backfill(EdgeGen(EdgeOptions()));

        Assert.Contains($"trg.[ValidTo_DW] = '{EdgeOpen}'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"trg.[ValidTo_DW] = '{EdgeAsOf}'", sql, StringComparison.Ordinal);
    }

    // ---- Close (statement 2) ----------------------------------------------------------------------------

    [Fact]
    public void Close_OmitsUpdatedDate_WhenNoUpdatedDateColumn()
    {
        var statements = EdgeGen(EdgeOptions() with { UpdatedDateColumn = null });
        var close = statements[1].Sql;

        Assert.Equal(UpsertStatementKind.Update, statements[1].Kind);
        Assert.Contains("trg.[IsCurrent_DW] = 0", close, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdatedDate_DW", close, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_OmitsRowStatus_WhenNoRowStatusColumn()
    {
        var close = EdgeGen(EdgeOptions() with { RowStatusColumn = null })[1].Sql;

        Assert.Contains($"trg.[ValidTo_DW] = '{EdgeAsOf}'", close, StringComparison.Ordinal);
        Assert.DoesNotContain("RowStatus_DW", close, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_StampsBothUpdatedDateAndRowStatus_WhenBothColumnsPresent()
    {
        var close = EdgeGen(EdgeOptions())[1].Sql;

        Assert.Contains($"trg.[UpdatedDate_DW] = '{EdgeAsOf}'", close, StringComparison.Ordinal);
        Assert.Contains("trg.[RowStatus_DW] = 'U'", close, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_ChecksumExcludesAllBusinessKeyColumns_ForACompositeKey()
    {
        // Composite key [CustomerId, Region]: the change predicate must compare only the non-key attributes,
        // never the keys (a key can never differ for a matched row, and hashing it is wasteful and wrong).
        var options = EdgeOptions() with
        {
            DataColumns = ["CustomerId", "Region", "Name", "City"],
            KeyColumns = ["CustomerId", "Region"],
        };
        var close = EdgeGen(options)[1].Sql;
        var predicate = close[close.IndexOf("HASHBYTES", StringComparison.Ordinal)..];

        Assert.Contains("src.[Name]", predicate, StringComparison.Ordinal);
        Assert.Contains("src.[City]", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("src.[CustomerId]", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("src.[Region]", predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_ChecksumInterleavesASeparator_ForMultipleTrackedColumns()
    {
        // Two distinct rows must not collide on concatenation, so a separator literal is interleaved between
        // the hashed columns. With Name and City tracked the separator N'|' must be present.
        var close = EdgeGen(EdgeOptions())[1].Sql;
        var predicate = close[close.IndexOf("HASHBYTES", StringComparison.Ordinal)..];

        Assert.Contains("N'|'", predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_IsSkipped_WhenExcludeFromChecksumRemovesEveryComparableColumn()
    {
        // Every non-key column is excluded from the checksum, so nothing is comparable: there is no change to
        // detect, the close step is dropped, and only backfill + insert remain.
        var options = EdgeOptions() with
        {
            ExcludeFromChecksum = new HashSet<string>(["Name", "City"], StringComparer.OrdinalIgnoreCase),
        };
        var statements = EdgeGen(options);

        Assert.Equal(2, statements.Count);
        Assert.Equal(UpsertStatementKind.Update, statements[0].Kind);
        Assert.Equal(UpsertStatementKind.Insert, statements[1].Kind);
    }

    [Fact]
    public void Close_ExcludeFromChecksum_WinsOverAnExplicitTrackedColumn()
    {
        // City is explicitly tracked but also excluded from the checksum (its type cannot CONCAT). The
        // exclusion must win, leaving nothing comparable, so no version is ever opened on a City change.
        var options = EdgeOptions() with
        {
            Scd2TrackedColumns = ["City"],
            ExcludeFromChecksum = new HashSet<string>(["City"], StringComparer.OrdinalIgnoreCase),
        };
        var statements = EdgeGen(options);

        Assert.Equal(2, statements.Count);
        Assert.DoesNotContain(statements, s => s.Sql.Contains("HASHBYTES", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("City", "City", false)]      // tracked column is real -> compared
    [InlineData("City", "Name", false)]      // the other real column is NOT compared
    [InlineData("Ghost", "Name", true)]      // a tracked name that is not a data column is ignored entirely
    public void Close_TrackedColumns_AreIntersectedWithRealNonKeyColumns(string tracked, string probe, bool closeSkipped)
    {
        var statements = EdgeGen(EdgeOptions() with { Scd2TrackedColumns = [tracked] });

        if (closeSkipped)
        {
            // A tracked set that resolves to no real comparable column drops the close step.
            Assert.Equal(2, statements.Count);
            return;
        }

        var close = statements[1].Sql;
        var predicate = close[close.IndexOf("HASHBYTES", StringComparison.Ordinal)..];
        var expectPresent = string.Equals(tracked, probe, StringComparison.Ordinal);
        Assert.Equal(expectPresent, predicate.Contains($"src.[{probe}]", StringComparison.Ordinal));
    }

    // ---- Insert (statement 3) ---------------------------------------------------------------------------

    [Fact]
    public void Insert_OmitsInsertedDate_WhenNoInsertedDateColumn()
    {
        var statements = EdgeGen(EdgeOptions() with { InsertedDateColumn = null });
        var insert = statements[^1].Sql;

        Assert.Equal(UpsertStatementKind.Insert, statements[^1].Kind);
        Assert.DoesNotContain("InsertedDate_DW", insert, StringComparison.Ordinal);
        // The new version is still current with the contiguous from/to/flag tuple.
        Assert.Contains($"'{EdgeAsOf}', '{EdgeOpen}', 1", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_StampsInsertedDateWithTheAsOf_NotSysUtcDateTime()
    {
        // The plain (non-SCD2) insert stamps InsertedDate with SYSUTCDATETIME(); the SCD2 insert must instead
        // use the inlined as-of so the audit date matches the period start exactly and the load is reproducible.
        var insert = EdgeGen(EdgeOptions())[^1].Sql;

        Assert.DoesNotContain("SYSUTCDATETIME()", insert, StringComparison.Ordinal);
        Assert.Contains($"[InsertedDate_DW]", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_OmitsRowStatus_WhenNoRowStatusColumn()
    {
        var insert = EdgeGen(EdgeOptions() with { RowStatusColumn = null })[^1].Sql;

        Assert.DoesNotContain("RowStatus_DW", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("'I'", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_DedupesByKeyViaRowNumber()
    {
        // The SCD2 insert reduces staging to one row per key via ROW_NUMBER (the same collapse the plain and
        // batched keyed inserts use), so a key appearing several times in staging versions once. It must never
        // emit SELECT DISTINCT.
        var insert = EdgeGen(EdgeOptions())[^1].Sql;

        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [CustomerId]", insert, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_PartitionsByEveryColumnOfACompositeKey()
    {
        var options = EdgeOptions() with
        {
            DataColumns = ["CustomerId", "Region", "Name", "City"],
            KeyColumns = ["CustomerId", "Region"],
        };
        var insert = EdgeGen(options)[^1].Sql;

        Assert.Contains("PARTITION BY [CustomerId], [Region]", insert, StringComparison.Ordinal);
        // The anti-join also matches on both key columns against the current row.
        Assert.Contains("src.[CustomerId] = trg.[CustomerId] AND src.[Region] = trg.[Region]", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_AntiJoinsOnlyAgainstTheCurrentRow()
    {
        // A changed key was just closed (its current flag cleared), and a brand-new key never had one, so the
        // anti-join must be scoped to current rows: an expired version must not block the new insert.
        var insert = EdgeGen(EdgeOptions())[^1].Sql;

        Assert.Contains("NOT EXISTS", insert, StringComparison.Ordinal);
        Assert.Contains("trg.[IsCurrent_DW] = 1", insert, StringComparison.Ordinal);
    }

    // ---- Cross-statement invariants ---------------------------------------------------------------------

    [Fact]
    public void CustomPeriodColumnNames_PropagateIntoAllThreeStatements()
    {
        var options = EdgeOptions() with
        {
            Scd2ValidFromColumn = "EffFrom",
            Scd2ValidToColumn = "EffTo",
            Scd2CurrentFlagColumn = "IsActive",
        };
        var statements = EdgeGen(options);

        Assert.All(statements, s => Assert.DoesNotContain("ValidFrom_DW", s.Sql, StringComparison.Ordinal));
        Assert.Contains("trg.[EffTo] = '" + EdgeOpen + "'", statements[0].Sql, StringComparison.Ordinal); // backfill
        Assert.Contains("trg.[IsActive] = 0", statements[1].Sql, StringComparison.Ordinal);               // close
        Assert.Contains("[EffFrom], [EffTo], [IsActive]", statements[^1].Sql, StringComparison.Ordinal);  // insert
    }

    [Fact]
    public void Scd2_IgnoresSkipUpdateAndSkipInsert()
    {
        // The plain upsert's SkipUpdate / SkipInsert have no meaning for SCD2 (the doc states they do not
        // apply): all three statements are emitted regardless.
        var statements = EdgeGen(EdgeOptions() with { SkipUpdate = true, SkipInsert = true });

        Assert.Equal(3, statements.Count);
        Assert.Equal(UpsertStatementKind.Update, statements[0].Kind);
        Assert.Equal(UpsertStatementKind.Update, statements[1].Kind);
        Assert.Equal(UpsertStatementKind.Insert, statements[2].Kind);
    }

    [Fact]
    public void Scd2_IgnoresBatchToAvoidLockEscalation_NoTempKeyTables()
    {
        // The batched lock-escalation apply is a plain-upsert concern; the SCD2 path emits straight DML with no
        // #UpsertKeys temp tables and reports counts the ordinary way (not from a scalar).
        var statements = EdgeGen(EdgeOptions() with { BatchToAvoidLockEscalation = true, BatchRowCount = 500 });

        Assert.All(statements, s => Assert.DoesNotContain("#UpsertKeys", s.Sql, StringComparison.Ordinal));
        Assert.All(statements, s => Assert.False(s.CountFromScalar));
    }

    [Fact]
    public void Scd2_AllStatementKinds_AreBackfillUpdate_CloseUpdate_InsertInsert()
    {
        var kinds = EdgeGen(EdgeOptions()).Select(s => s.Kind).ToArray();

        Assert.Equal([UpsertStatementKind.Update, UpsertStatementKind.Update, UpsertStatementKind.Insert], kinds);
    }

    [Theory]
    [InlineData("MD5")]
    [InlineData("SHA1")]
    [InlineData("SHA2_512")]
    public void Scd2_ChecksumUsesTheConfiguredHashAlgorithm(string algorithm)
    {
        var close = EdgeGen(EdgeOptions() with { HashAlgorithm = algorithm })[1].Sql;

        Assert.Contains($"HASHBYTES('{algorithm}'", close, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2_RejectsAnUnknownHashAlgorithm()
    {
        var ex = Assert.Throws<SqlFlowException>(() => EdgeGen(EdgeOptions() with { HashAlgorithm = "CRC32" }));
        Assert.Contains("CRC32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2_RejectsEmptyKeyColumns()
    {
        var ex = Assert.Throws<SqlFlowException>(() => EdgeGen(EdgeOptions() with { KeyColumns = [] }));
        Assert.Contains("key column", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scd2_RejectsAKeyThatIsNotAmongTheDataColumns()
    {
        var ex = Assert.Throws<SqlFlowException>(() =>
            EdgeGen(EdgeOptions() with { KeyColumns = ["MissingKey"] }));
        Assert.Contains("MissingKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2_RequiresAnAsOfInstant()
    {
        var ex = Assert.Throws<SqlFlowException>(() => EdgeGen(EdgeOptions() with { Scd2AsOfLiteral = null }));
        Assert.Contains("as-of", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("flag")]
    public void Scd2_RequiresEachPeriodColumn(string missing)
    {
        var options = EdgeOptions() with
        {
            Scd2ValidFromColumn = missing == "from" ? null : "ValidFrom_DW",
            Scd2ValidToColumn = missing == "to" ? null : "ValidTo_DW",
            Scd2CurrentFlagColumn = missing == "flag" ? null : "IsCurrent_DW",
        };

        Assert.Throws<SqlFlowException>(() => EdgeGen(options));
    }

    [Fact]
    public void Scd2_EscapesABracketInAColumnName()
    {
        // A right-bracket in an identifier must be doubled (]]), or the generated T-SQL would break out of the
        // delimiter. Use a weird-but-legal data column and confirm it is escaped wherever it lands.
        var options = EdgeOptions() with { DataColumns = ["CustomerId", "Wei]rd"] };
        var insert = EdgeGen(options)[^1].Sql;

        Assert.Contains("[Wei]]rd]", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("[Wei]rd]", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2_EscapesABracketInThePeriodColumnName()
    {
        var options = EdgeOptions() with { Scd2CurrentFlagColumn = "Is]Current" };
        var backfill = Backfill(EdgeGen(options));

        Assert.Contains("trg.[Is]]Current] = 1", backfill, StringComparison.Ordinal);
    }

    // ---- CanonicalIndexPlanner --------------------------------------------------------------------------

    [Fact]
    public void Plan_UnderScd2_StillEmitsTheNonKeyIndexes()
    {
        // SCD2 only suppresses the plain UNIQUE key index (replaced by the filtered one); the date, dataset and
        // UpdatedDate auxiliary indexes are unaffected.
        var statements = CanonicalIndexPlanner.Plan(
            EdgeTarget, ["CustomerId"], dateColumn: "ValidFrom_DW", dataSetColumn: "DataSet_DW",
            hasUpdatedDateColumn: true, columnStore: false, hasIdentityPrimaryKey: false, scd2Enabled: true);
        var sql = string.Join("\n", statements);

        Assert.DoesNotContain("UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[NCI_DateColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[NCI_DataSetColumn]", sql, StringComparison.Ordinal);
        Assert.Contains("[NCI_UpdatedDate_DW]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithoutScd2_EmitsThePlainUniqueKeyIndex()
    {
        // The contrast case: with SCD2 off the key is unique table-wide, so the plain unique NCI_KeyColumn is
        // emitted and the index is NOT filtered on a current flag (no WHERE [...] = 1 partial-index clause).
        var statements = CanonicalIndexPlanner.Plan(
            EdgeTarget, ["CustomerId"], dateColumn: null, dataSetColumn: null,
            hasUpdatedDateColumn: false, columnStore: false, hasIdentityPrimaryKey: false, scd2Enabled: false);
        var sql = string.Join("\n", statements);

        Assert.Contains("CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("] = 1;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_ColumnStoreIsSuppressedByAnIdentityPrimaryKey_RegardlessOfScd2()
    {
        var statements = CanonicalIndexPlanner.Plan(
            EdgeTarget, ["CustomerId"], dateColumn: null, dataSetColumn: null,
            hasUpdatedDateColumn: false, columnStore: true, hasIdentityPrimaryKey: true, scd2Enabled: true);
        var sql = string.Join("\n", statements);

        Assert.DoesNotContain("COLUMNSTORE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndex_ReturnsNothing_WhenThereAreNoKeyColumns()
    {
        var statements = CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget, [], "IsCurrent_DW");

        Assert.Empty(statements);
    }

    [Fact]
    public void Scd2KeyIndex_FiltersOnTheCustomCurrentFlagColumn()
    {
        var sql = string.Join("\n", CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget, ["CustomerId"], "IsActive"));

        Assert.Contains("WHERE [IsActive] = 1;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndex_CompositeKey_CountsAndListsEveryKeyColumn()
    {
        var sql = string.Join("\n", CanonicalIndexPlanner.Scd2KeyIndexStatements(
            EdgeTarget, ["CustomerId", "Region"], "IsCurrent_DW"));

        // The discovery predicate matches an index whose key width equals the composite count, and both key
        // names are listed both as the NOT IN exclusion and in the CREATE column list.
        Assert.Contains("is_included_column = 0) = 2", sql, StringComparison.Ordinal);
        Assert.Contains("c.name NOT IN (N'CustomerId', N'Region')", sql, StringComparison.Ordinal);
        Assert.Contains("([CustomerId], [Region])", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndex_EscapesBracketsInKeyAndFlagNames()
    {
        var weird = new RelationalObject { Database = "DW", Schema = "di]m", Name = "Cus]tomer" };
        var sql = string.Join("\n", CanonicalIndexPlanner.Scd2KeyIndexStatements(weird, ["Ke]y"], "Is]Current"));

        Assert.Contains("[di]]m].[Cus]]tomer]", sql, StringComparison.Ordinal);   // qualified name bracket-escaped
        Assert.Contains("([Ke]]y])", sql, StringComparison.Ordinal);              // key column bracket-escaped
        Assert.Contains("WHERE [Is]]Current] = 1;", sql, StringComparison.Ordinal); // flag bracket-escaped
        // Inside a string literal a bracket is NOT doubled (only single quotes are), so the discovery literal
        // carries the raw bracket: the name comparison runs against sys.columns.name, which is the bare name.
        Assert.Contains("N'Ke]y'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2KeyIndex_RejectsABlankCurrentFlagColumn()
    {
        Assert.Throws<ArgumentException>(() =>
            CanonicalIndexPlanner.Scd2KeyIndexStatements(EdgeTarget, ["CustomerId"], "   "));
    }

    // ---- YAML versioning.scd2 surface -------------------------------------------------------------------

    private static string EdgeDoc(string versioning, string load = "  keyColumns: [CustomerId]") => string.Create(
        CultureInfo.InvariantCulture,
        $"""
        flowType: ing
        name: dim_customer
        connections:
          SRC:
          DW:
        source:
          server: SRC
          object: Src.dbo.Customer
        target:
          server: DW
          object: DW.dim.Customer
        load:
        {load}
        {versioning}
        """);

    [Theory]
    [InlineData("Period", "Period", "IsCurrent_DW")]   // from == to
    [InlineData("ValidFrom_DW", "Same", "Same")]       // to == flag
    [InlineData("Dup", "ValidTo_DW", "Dup")]           // from == flag
    [InlineData("Col", "COL", "IsCurrent_DW")]         // case-insensitive collision (from vs to)
    public void Yaml_RejectsAnyCollisionAmongThePeriodColumnNames(string from, string to, string flag)
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlIngestionFlowLoader().Parse(EdgeDoc($"""
            versioning:
              scd2:
                enabled: true
                validFromColumn: {from}
                validToColumn: {to}
                currentFlagColumn: {flag}
            """)));

        Assert.Contains("distinct", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Yaml_BlankTrackedColumnsAreDropped()
    {
        var doc = new YamlIngestionFlowLoader().Parse(EdgeDoc("""
            versioning:
              scd2:
                enabled: true
                trackedColumns: ["City", "  ", "", "Segment"]
            """));

        Assert.Equal(["City", "Segment"], doc.Flow.Versioning.Scd2.TrackedColumns);
    }

    [Fact]
    public void Yaml_EnabledScd2WithNoOverrides_UsesTheCanonicalDefaultColumns()
    {
        var doc = new YamlIngestionFlowLoader().Parse(EdgeDoc("""
            versioning:
              scd2:
                enabled: true
            """));
        var scd2 = doc.Flow.Versioning.Scd2;

        Assert.True(scd2.Enabled);
        Assert.Equal("ValidFrom_DW", scd2.ValidFromColumn);
        Assert.Equal("ValidTo_DW", scd2.ValidToColumn);
        Assert.Equal("IsCurrent_DW", scd2.CurrentFlagColumn);
        Assert.Empty(scd2.TrackedColumns);
    }

    [Fact]
    public void Yaml_Scd2DisabledDoesNotRequireKeyColumns()
    {
        // The key requirement only fires when SCD2 is ENABLED. A disabled block (custom column names but no
        // versioning) must parse even with no load.keyColumns.
        var doc = new YamlIngestionFlowLoader().Parse(EdgeDoc("""
            versioning:
              scd2:
                enabled: false
                validFromColumn: EffFrom
                validToColumn: EffTo
            """, load: "  threads: 1"));

        Assert.False(doc.Flow.Versioning.Scd2.Enabled);
        // The custom names are still mapped through even though the feature is off.
        Assert.Equal("EffFrom", doc.Flow.Versioning.Scd2.ValidFromColumn);
    }
}
