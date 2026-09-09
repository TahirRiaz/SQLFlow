using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Edge-case hardening for the keyed two-step upsert generator: composite and case-insensitive keys, identifier
/// escaping (including a literal ']'), reserved-word and large column lists, the empty/normalized hash-algorithm
/// boundary, the skip-both no-op, the system-column-only branches, and the batched-window combinations. Every
/// case is pure text generation, asserted without a database, and is distinct from the cases already covered by
/// UpsertGeneratorTests, UpsertGeneratorUpgradeTests, and Scd2UpsertGeneratorTests.
/// </summary>
public sealed class UpsertEdgeCaseTests
{
    private static RelationalObject UecObj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static readonly RelationalObject UecTarget = new() { Database = "db", Schema = "dbo", Name = "Trg" };
    private static readonly RelationalObject UecStaging = new() { Database = "db", Schema = "dbo", Name = "Stg" };

    private static UpsertOptions UecOptions(
        IReadOnlyList<string> data,
        IReadOnlyList<string> keys,
        bool skipUpdate = false,
        bool skipInsert = false)
        => new() { DataColumns = data, KeyColumns = keys, SkipUpdate = skipUpdate, SkipInsert = skipInsert };

    private static UpsertStatement UecUpdate(IReadOnlyList<UpsertStatement> statements)
        => Assert.Single(statements, s => s.Kind == UpsertStatementKind.Update);

    private static UpsertStatement UecInsert(IReadOnlyList<UpsertStatement> statements)
        => Assert.Single(statements, s => s.Kind == UpsertStatementKind.Insert);

    private static int UecCount(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // --- Composite keys -------------------------------------------------------------------------------------

    [Fact]
    public void CompositeKey_JoinIsAndedAcrossEveryKey()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["TenantId", "Id", "Name"], ["TenantId", "Id"])));

        Assert.Contains("src.[TenantId] = trg.[TenantId] AND src.[Id] = trg.[Id]", update.Sql, StringComparison.Ordinal);
        // The remaining non-key column is the only thing copied and compared.
        Assert.Contains("trg.[Name] = src.[Name]", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeKey_InsertAntiJoinUsesEveryKey()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["TenantId", "Id", "Name"], ["TenantId", "Id"])));

        Assert.Contains(
            "NOT EXISTS (SELECT 1 FROM [dbo].[Trg] AS trg WHERE src.[TenantId] = trg.[TenantId] AND src.[Id] = trg.[Id])",
            insert.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeKey_OneKeyMissingFromData_Throws()
    {
        // TenantId is a key but is not among the data columns: the upsert cannot copy or match on it.
        var ex = Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(
            UecTarget, UecStaging, UecOptions(["Id", "Name"], ["Id", "TenantId"])));
        Assert.Contains("TenantId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllColumnsAreKeys_Composite_ProducesOnlyInsert()
    {
        // Every column is part of the key, so there is no non-key attribute to update.
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["TenantId", "Id"], ["TenantId", "Id"]));
        var only = Assert.Single(statements);
        Assert.Equal(UpsertStatementKind.Insert, only.Kind);
    }

    // --- Case-insensitive key matching ----------------------------------------------------------------------

    [Fact]
    public void KeyMatchesDataColumnCaseInsensitively_RemovesItFromTheUpdateSet()
    {
        // Key "id" matches data column "ID" ignoring case, so "ID" is treated as the key and never appears in
        // the SET list; only the genuine non-key column is copied.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["ID", "Name"], ["id"])));

        Assert.Contains("trg.[Name] = src.[Name]", update.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("trg.[ID] = src.[ID]", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyMatchesAllDataColumnsCaseInsensitively_ProducesOnlyInsert()
    {
        // The only data column "ID" is the key (differing only in case), so there is nothing to update.
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, UecOptions(["ID"], ["id"]));
        Assert.Equal(UpsertStatementKind.Insert, Assert.Single(statements).Kind);
    }

    // --- Identifier escaping --------------------------------------------------------------------------------

    [Fact]
    public void ColumnNameWithClosingBracket_IsEscapedEverywhere()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "We]rd"], ["Id"])));

        // The ']' is doubled to ']]' inside the brackets in both the SET assignment and the checksum.
        Assert.Contains("trg.[We]]rd] = src.[We]]rd]", update.Sql, StringComparison.Ordinal);
        Assert.Contains("src.[We]]rd]", update.Sql[update.Sql.IndexOf("HASHBYTES", StringComparison.Ordinal)..], StringComparison.Ordinal);
        // The raw single-bracket form must never leak through.
        Assert.DoesNotContain("[We]rd]", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyNameWithClosingBracket_IsEscapedInTheJoin()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["K]1", "Name"], ["K]1"])));

        Assert.Contains("src.[K]]1] = trg.[K]]1]", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetAndStagingNamesWithBrackets_AreEscapedAndQualifiedWithoutDatabase()
    {
        // The qualifier uses [Schema].[Name] only (the database part is intentionally dropped), and a ']' in a
        // name is doubled.
        var target = new RelationalObject { Database = "ServerDb", Schema = "my]schema", Name = "ta]ble" };
        var statements = UpsertGenerator.GenerateStatements(target, UecStaging, UecOptions(["Id", "Name"], ["Id"]));

        var insert = UecInsert(statements);
        Assert.Contains("INSERT INTO [my]]schema].[ta]]ble]", insert.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerDb", insert.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Order Date")]
    [InlineData("Full.Stop")]
    [InlineData("Tab\tName")]
    [InlineData("Æøå_Name")]
    [InlineData("col'quote")]
    public void ColumnNamesWithSpecialCharacters_AreCopiedVerbatimInsideBrackets(string oddColumn)
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", oddColumn], ["Id"])));

        // Nothing but ']' is special inside a bracketed identifier, so the name is copied through unchanged.
        Assert.Contains($"trg.[{oddColumn}] = src.[{oddColumn}]", update.Sql, StringComparison.Ordinal);
    }

    // --- Reserved words -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Select")]
    [InlineData("From")]
    [InlineData("Order")]
    [InlineData("Table")]
    [InlineData("User")]
    [InlineData("Group")]
    public void ReservedWordColumnNames_AreBracketedInBothBranches(string reserved)
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", reserved], ["Id"]));

        Assert.Contains($"[{reserved}]", UecUpdate(statements).Sql, StringComparison.Ordinal);
        Assert.Contains($"[{reserved}]", UecInsert(statements).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedWordKeyName_IsBracketedInTheKeyEquality()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Key", "Name"], ["Key"])));

        Assert.Contains("src.[Key] = trg.[Key]", update.Sql, StringComparison.Ordinal);
    }

    // --- Null-argument guards -------------------------------------------------------------------------------

    [Fact]
    public void NullTarget_Throws()
        => Assert.Throws<ArgumentNullException>(() => UpsertGenerator.GenerateStatements(
            null!, UecStaging, UecOptions(["Id", "Name"], ["Id"])));

    [Fact]
    public void NullStaging_Throws()
        => Assert.Throws<ArgumentNullException>(() => UpsertGenerator.GenerateStatements(
            UecTarget, null!, UecOptions(["Id", "Name"], ["Id"])));

    [Fact]
    public void NullOptions_Throws()
        => Assert.Throws<ArgumentNullException>(() => UpsertGenerator.GenerateStatements(UecTarget, UecStaging, null!));

    // --- Generate vs GenerateStatements parity --------------------------------------------------------------

    [Fact]
    public void Generate_ReturnsTheSameSqlStringsInTheSameOrderAsGenerateStatements()
    {
        var options = UecOptions(["Id", "Name", "Amount"], ["Id"]);
        var strings = UpsertGenerator.Generate(UecTarget, UecStaging, options);
        var tagged = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, options);

        Assert.Equal(tagged.Select(s => s.Sql), strings);
    }

    // --- Hash algorithm boundaries --------------------------------------------------------------------------

    [Theory]
    [InlineData("SHA2_512")]
    [InlineData("sha2_512")]
    [InlineData("Sha2_256")]
    [InlineData("SHA1")]
    [InlineData("MD5")]
    [InlineData("md5")]
    public void KnownHashAlgorithm_IsInlinedVerbatim(string algorithm)
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = algorithm }));

        // The original (un-normalized) string is what gets interpolated into HASHBYTES.
        Assert.Contains($"HASHBYTES('{algorithm}',", update.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SHA3_256")]
    [InlineData("MD6")]
    [InlineData("xxhash")]
    [InlineData("SHA2-256")]
    public void UnknownHashAlgorithm_Throws(string algorithm)
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = algorithm }));

    [Fact]
    public void WhitespacePaddedHashAlgorithm_IsAcceptedAndInlinedWithItsPadding()
    {
        // BinaryTypeFor trims before validating, so a padded name passes; the generator still inlines the raw
        // string, so the padding survives into the SQL.
        const string padded = "  SHA2_256  ";
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = padded }));

        Assert.Contains($"HASHBYTES('{padded}',", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyHashAlgorithm_DoesNotThrowButInlinesAnEmptyAlgorithmName()
    {
        // An empty/whitespace algorithm is treated as the default for validation (no throw), yet the empty
        // string is what gets inlined into HASHBYTES. Asserting the current behavior; an empty HASHBYTES
        // algorithm is not valid T-SQL, so this is recorded as a suspected gap.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            new UpsertOptions { DataColumns = ["Id", "Name"], KeyColumns = ["Id"], HashAlgorithm = string.Empty }));

        Assert.Contains("HASHBYTES('',", update.Sql, StringComparison.Ordinal);
    }

    // --- Skip combinations ----------------------------------------------------------------------------------

    [Fact]
    public void SkipUpdateAndSkipInsert_ProducesNoStatements()
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "Name"], ["Id"], skipUpdate: true, skipInsert: true));
        Assert.Empty(statements);
    }

    [Fact]
    public void SkipUpdate_WithBatching_EmitsOnlyTheBatchedInsert()
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            SkipUpdate = true,
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 250,
        });

        var only = Assert.Single(statements);
        Assert.Equal(UpsertStatementKind.Insert, only.Kind);
        Assert.True(only.CountFromScalar);
        Assert.Contains("#UpsertKeysI", only.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipInsert_WithBatching_EmitsOnlyTheBatchedUpdate()
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            SkipInsert = true,
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 250,
        });

        var only = Assert.Single(statements);
        Assert.Equal(UpsertStatementKind.Update, only.Kind);
        Assert.True(only.CountFromScalar);
        Assert.Contains("#UpsertKeysU", only.Sql, StringComparison.Ordinal);
    }

    // --- System-column-only branches ------------------------------------------------------------------------

    [Fact]
    public void InsertedDateColumnAlone_StampsInsertButNotRowStatus()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
        }));

        Assert.Contains("[InsertedDate_DW]", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("SYSUTCDATETIME()", insert.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'I'", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RowStatusColumnAlone_StampsInsertWithLiteralIButNoDate()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            RowStatusColumn = "RowStatus_DW",
        }));

        Assert.Contains("[RowStatus_DW]", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("'I'", insert.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME()", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdatedDateColumnAlone_StampsUpdateButLeavesNoInsertedDateBackfillOrRowStatus()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            UpdatedDateColumn = "UpdatedDate_DW",
        }));

        Assert.Contains("trg.[UpdatedDate_DW] = SYSUTCDATETIME()", update.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("RowStatus", update.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NULL THEN SYSUTCDATETIME()", update.Sql, StringComparison.Ordinal);
    }

    // --- Checksum shape -------------------------------------------------------------------------------------

    [Fact]
    public void SingleNonKeyColumn_ChecksumStillGuardsConcatWithLeadingEmptyString()
    {
        // CONCAT needs two arguments, so even a single comparable column is preceded by N''.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, UecOptions(["Id", "Name"], ["Id"])));

        Assert.Contains("CONCAT(N'', src.[Name])", update.Sql, StringComparison.Ordinal);
        Assert.Contains("CONCAT(N'', trg.[Name])", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleNonKeyColumns_AreSeparatedBySentinelInTheChecksum()
    {
        // Two comparable columns are interleaved with exactly one N'|' separator so distinct rows cannot collide.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "Name", "Amount"], ["Id"])));

        var checksum = update.Sql[update.Sql.IndexOf("CONCAT", StringComparison.Ordinal)..];
        Assert.Contains("CONCAT(N'', src.[Name], N'|', src.[Amount])", update.Sql, StringComparison.Ordinal);
        Assert.Equal(1, UecCount(checksum[..checksum.IndexOf(')', StringComparison.Ordinal)], "N'|'"));
    }

    // --- Exclude-from-checksum interactions -----------------------------------------------------------------

    [Fact]
    public void ExcludeFromChecksum_IsCaseInsensitive()
    {
        // Excluding "amount" (lower case) removes "Amount" from the change predicate, because the default
        // exclusion set compares ignoring case.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name", "Amount"],
            KeyColumns = ["Id"],
            ExcludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "amount" },
        }));

        var checksum = update.Sql[update.Sql.IndexOf("HASHBYTES", StringComparison.Ordinal)..];
        Assert.DoesNotContain("[Amount]", checksum, StringComparison.Ordinal);
        Assert.Contains("[Name]", checksum, StringComparison.Ordinal);
        // It is still copied by the SET list.
        Assert.Contains("trg.[Amount] = src.[Amount]", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludingAKeyColumn_HasNoEffectBecauseKeysAreNeverInTheChecksum()
    {
        // Excluding the key from the checksum changes nothing: the change predicate still compares the non-key
        // columns and the update is still produced.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name", "Amount"],
            KeyColumns = ["Id"],
            ExcludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id" },
        }));

        var checksum = update.Sql[update.Sql.IndexOf("HASHBYTES", StringComparison.Ordinal)..];
        Assert.Contains("src.[Name]", checksum, StringComparison.Ordinal);
        Assert.Contains("src.[Amount]", checksum, StringComparison.Ordinal);
    }

    // --- Per-key staging dedupe on insert -------------------------------------------------------------------

    [Fact]
    public void PlainInsert_CollapsesStagingToOneRowPerKey()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "Name"], ["Id"])));

        // Partition by key + keep _rn = 1: one row per key survives, so two same-key staging rows cannot both
        // be inserted and collide on the target's unique key. A full-row SELECT DISTINCT would not achieve this.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND NOT EXISTS", insert.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedInsert_CollapsesStagingToOneRowPerKeyInsideTheWindow()
    {
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 1000,
        }));

        // The windowed insert reads from the per-key-deduped staging source, so a key window inserts one row
        // per key even when staging holds several rows for that key.
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND k.RowNum BETWEEN @Start AND @End", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedInsert_NumbersDistinctKeys_NotDistinctRows()
    {
        // Regression: the key table used to be built as `SELECT DISTINCT <keys>, ROW_NUMBER() OVER (...)`.
        // ROW_NUMBER is computed BEFORE the DISTINCT and is unique on every row, so the DISTINCT removed
        // nothing: a key staged twice produced two key-table rows, the window join multiplied the deduped
        // staging row back out, and the INSERT hit the target's unique key with its own second copy.
        // The numbering must sit OUTSIDE the DISTINCT.
        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 1000,
        }));

        Assert.DoesNotContain("SELECT DISTINCT src.[Id], ROW_NUMBER()", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT k.[Id], ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum", insert.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT DISTINCT src.[Id]", insert.Sql, StringComparison.Ordinal);
        Assert.Contains(") AS k;", insert.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedUpdate_NumbersDistinctKeys_AndReadsOneRowPerKey()
    {
        // The same defect on the UPDATE side is not a hard error (SQL Server allows a multi-matched UPDATE)
        // but it writes the same target row once per staging copy, leaves an undefined winner, and reports
        // the staging match count as the number of rows changed.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 1000,
        }));

        Assert.Contains("SELECT k.[Id], ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum", update.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM (SELECT DISTINCT src.[Id] FROM", update.Sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", update.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND k.RowNum BETWEEN @Start AND @End", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnbatchedUpdate_CollapsesStagingToOneRowPerKey()
    {
        // The batched and unbatched applies must agree: both write each matched target row exactly once.
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "Name"], ["Id"])));

        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY [Id]", update.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND ", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedWindows_JoinStagingBackNullSafely()
    {
        // A nullable business key never matches a target row (plain `=`), so it always reaches the INSERT key
        // table. Joining it back to staging with a plain `=` would then match nothing and the row would be
        // dropped from the load without a trace, only on the batched path.
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 1000,
        });

        foreach (var statement in statements)
        {
            Assert.Contains(
                "(src.[Id] = k.[Id] OR (src.[Id] IS NULL AND k.[Id] IS NULL))",
                statement.Sql,
                StringComparison.Ordinal);
        }
    }

    // --- Batched-window combinations ------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void Batched_NonPositiveBatchSize_Throws(int batchRowCount)
        => Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = batchRowCount,
        }));

    [Fact]
    public void Batched_BatchSizeOfOne_IsAccepted()
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 1,
        });

        var update = UecUpdate(statements);
        Assert.Contains("@End bigint = 1", update.Sql, StringComparison.Ordinal);
        Assert.Contains("SET @End = @End + 1;", update.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_CompositeKey_IndexesAndWindowsOnEveryKey()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["TenantId", "Id", "Name"],
            KeyColumns = ["TenantId", "Id"],
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 500,
        }));

        Assert.Contains("CREATE STATISTICS [ST_UpsertKeysU] ON #UpsertKeysU ([TenantId], [Id])", update.Sql, StringComparison.Ordinal);

        // The window join is NULL-safe on every key, so a nullable key column cannot drop its own row.
        Assert.Contains(
            "(src.[TenantId] = k.[TenantId] OR (src.[TenantId] IS NULL AND k.[TenantId] IS NULL)) "
            + "AND (src.[Id] = k.[Id] OR (src.[Id] IS NULL AND k.[Id] IS NULL))",
            update.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Batched_AllColumnsExcluded_DropsTheChangePredicateInsideTheWindow()
    {
        // With every comparable column excluded there is no checksum, so the windowed UPDATE filters on the row
        // number alone (no trailing AND change predicate).
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name", "Amount"],
            KeyColumns = ["Id"],
            ExcludeFromChecksum = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Name", "Amount" },
            BatchToAvoidLockEscalation = true,
            BatchRowCount = 500,
        }));

        Assert.DoesNotContain("HASHBYTES", update.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE src._rn = 1 AND k.RowNum BETWEEN @Start AND @End;", update.Sql, StringComparison.Ordinal);
    }

    // --- Large column list ----------------------------------------------------------------------------------

    [Fact]
    public void LargeColumnList_CopiesEveryNonKeyAndPutsEveryNonKeyInTheChecksum()
    {
        const int columnCount = 60;
        var columns = new List<string> { "Id" };
        for (var i = 0; i < columnCount; i++)
        {
            columns.Add("Col" + i.ToString(CultureInfo.InvariantCulture));
        }

        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, UecOptions(columns, ["Id"]));
        var update = UecUpdate(statements);
        var insert = UecInsert(statements);
        var checksum = update.Sql[update.Sql.IndexOf("CONCAT", StringComparison.Ordinal)..];

        for (var i = 0; i < columnCount; i++)
        {
            var col = "Col" + i.ToString(CultureInfo.InvariantCulture);
            Assert.Contains($"trg.[{col}] = src.[{col}]", update.Sql, StringComparison.Ordinal);
            Assert.Contains($"src.[{col}]", checksum, StringComparison.Ordinal);
            Assert.Contains($"src.[{col}]", insert.Sql, StringComparison.Ordinal);
        }

        // 60 non-key columns are joined by 59 separators on each side of the comparison.
        var srcChecksum = checksum[..checksum.IndexOf(')', StringComparison.Ordinal)];
        Assert.Equal(columnCount - 1, UecCount(srcChecksum, "N'|'"));
    }

    [Fact]
    public void LargeColumnList_InsertColumnAndSelectListsAreBalanced()
    {
        const int columnCount = 40;
        var columns = new List<string> { "Id" };
        for (var i = 0; i < columnCount; i++)
        {
            columns.Add("Field" + i.ToString(CultureInfo.InvariantCulture));
        }

        var insert = UecInsert(UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = columns,
            KeyColumns = ["Id"],
            InsertedDateColumn = "InsertedDate_DW",
            RowStatusColumn = "RowStatus_DW",
        }));

        // The column list and the SELECT list must agree in arity (data columns + the two system columns).
        var columnList = insert.Sql[(insert.Sql.IndexOf('(', StringComparison.Ordinal) + 1)..insert.Sql.IndexOf(')', StringComparison.Ordinal)];
        var selectStart = insert.Sql.IndexOf("SELECT ", StringComparison.Ordinal) + "SELECT ".Length;
        var selectList = insert.Sql[selectStart..insert.Sql.IndexOf(" FROM ", StringComparison.Ordinal)];

        var expected = (columnCount + 1) + 2;
        Assert.Equal(expected, UecCount(columnList, ",") + 1);
        Assert.Equal(expected, UecCount(selectList, ",") + 1);
    }

    // --- Non-key ordering -----------------------------------------------------------------------------------

    [Fact]
    public void NonKeyColumns_PreserveTheirDeclaredOrderInTheSetList()
    {
        var update = UecUpdate(UpsertGenerator.GenerateStatements(UecTarget, UecStaging,
            UecOptions(["Id", "Zeta", "Alpha", "Mid"], ["Id"])));

        var zeta = update.Sql.IndexOf("trg.[Zeta]", StringComparison.Ordinal);
        var alpha = update.Sql.IndexOf("trg.[Alpha]", StringComparison.Ordinal);
        var mid = update.Sql.IndexOf("trg.[Mid]", StringComparison.Ordinal);
        Assert.True(zeta < alpha && alpha < mid, "Non-key SET assignments should follow the declared column order.");
    }

    // --- SCD2 validation (distinct from the SCD2 happy-path suite) -------------------------------------------

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Scd2_MissingRequiredColumnOrInstant_Throws(bool from, bool to, bool flag, bool asOf)
    {
        var options = new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            Scd2Enabled = true,
            Scd2ValidFromColumn = from ? "ValidFrom_DW" : null,
            Scd2ValidToColumn = to ? "ValidTo_DW" : null,
            Scd2CurrentFlagColumn = flag ? "IsCurrent_DW" : null,
            Scd2AsOfLiteral = asOf ? "2026-06-16 11:22:33.444" : null,
        };

        Assert.Throws<SqlFlowException>(() => UpsertGenerator.GenerateStatements(UecTarget, UecStaging, options));
    }

    [Fact]
    public void Scd2_PeriodColumnWithBracket_IsEscapedInTheBackfill()
    {
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            Scd2Enabled = true,
            Scd2ValidFromColumn = "Valid]From",
            Scd2ValidToColumn = "Valid]To",
            Scd2CurrentFlagColumn = "Is]Current",
            Scd2AsOfLiteral = "2026-06-16 11:22:33.444",
        });

        var backfill = statements[0].Sql;
        Assert.Contains("trg.[Valid]]To] IS NULL", backfill, StringComparison.Ordinal);
        Assert.Contains("trg.[Is]]Current] = 1", backfill, StringComparison.Ordinal);
    }

    [Fact]
    public void Scd2_IgnoresSkipFlags_StillEmitsAllThreeStatements()
    {
        // SkipUpdate / SkipInsert are documented as not applying to the SCD2 load.
        var statements = UpsertGenerator.GenerateStatements(UecTarget, UecStaging, new UpsertOptions
        {
            DataColumns = ["Id", "Name"],
            KeyColumns = ["Id"],
            SkipUpdate = true,
            SkipInsert = true,
            Scd2Enabled = true,
            Scd2ValidFromColumn = "ValidFrom_DW",
            Scd2ValidToColumn = "ValidTo_DW",
            Scd2CurrentFlagColumn = "IsCurrent_DW",
            Scd2AsOfLiteral = "2026-06-16 11:22:33.444",
        });

        Assert.Equal(3, statements.Count);
    }

    // --- Different source/target identity -------------------------------------------------------------------

    [Fact]
    public void TargetAndStagingResolveToDifferentNames_InTheGeneratedSql()
    {
        var statements = UpsertGenerator.GenerateStatements(UecObj("DimCustomer"), UecObj("stg_DimCustomer"),
            UecOptions(["Id", "Name"], ["Id"]));

        var update = UecUpdate(statements);
        Assert.Contains("FROM [dbo].[stg_DimCustomer])", update.Sql, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN [dbo].[DimCustomer] AS trg", update.Sql, StringComparison.Ordinal);
    }
}
