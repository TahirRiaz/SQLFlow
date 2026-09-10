using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge-case coverage for schema evolution: the planner, the type-resolution widening rules,
/// the change-footprint classifier, and the DDL generator. Every test is pure and in-memory (no database, no
/// network) and uses fixed inputs so it is deterministic. These cases are intentionally distinct from the
/// happy-path and headline cases in the sibling SchemaEvolution* test files.
/// </summary>
public sealed class SchemaEvolutionEdgeCaseTests
{
    private static SqlColumn EvoCol(string name, string type, bool nullable = true, ColumnRole role = ColumnRole.Source, bool pk = false, bool identity = false)
        => new()
        {
            Name = name,
            DataType = SqlDataType.Parse(type),
            IsNullable = nullable,
            Role = role,
            IsPrimaryKey = pk,
            IsIdentity = identity,
        };

    private static readonly IReadOnlySet<string> EvoNoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> EvoKeys(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static readonly RelationalObject EvoTarget = RelationalObject.Parse("[Db].[dbo].[Customer]");

    private static EvolutionPlan PlanOne(string actualType, string desiredType, IReadOnlySet<string>? keys = null, ColumnRole role = ColumnRole.Source)
        => SchemaEvolutionPlanner.Plan(
            [EvoCol("C", desiredType, role: role)],
            [EvoCol("C", actualType)],
            keys ?? EvoNoKeys);

    // ---- Type resolution: widening across sub-types within a family -----------------------------------

    [Fact]
    public void VarcharToNvarchar_WidensToUnicode_MetadataOnly()
    {
        var plan = PlanOne("varchar(50)", "nvarchar(50)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("nvarchar(50)", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
        Assert.False(plan.HasRewrite);
    }

    [Fact]
    public void CharToVarchar_BecomesVariable_KeepsWiderLength()
    {
        var plan = PlanOne("char(10)", "varchar(5)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("varchar(10)", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
    }

    [Fact]
    public void NvarcharActual_VarcharDesired_AlreadyUnicodeAndWider_NoChange()
    {
        // Existing nvarchar(100) already accommodates an incoming varchar(50): widened type equals existing.
        var plan = PlanOne("nvarchar(100)", "varchar(50)");
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.ColumnsToAlter);
    }

    [Fact]
    public void IntegerNarrowing_TargetAlreadyWider_NoChange()
    {
        var plan = PlanOne("bigint", "int");
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Drift);
    }

    [Fact]
    public void DecimalScaleWiden_RecomputesPrecisionWithinClass_MetadataOnly()
    {
        // existing decimal(10,2): 8 integer digits; incoming decimal(10,4): 6 integer digits, scale 4.
        // merged integer digits = 8, scale = 4 => decimal(12,4), same storage class as decimal(10,2).
        var plan = PlanOne("decimal(10,2)", "decimal(10,4)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("decimal(12, 4)", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
    }

    [Fact]
    public void DecimalScaleWiden_CrossingStorageClass_IsRewrite()
    {
        // existing decimal(9,2): class 0. incoming decimal(9,6): merged precision 7+6=13 => class 1.
        var plan = PlanOne("decimal(9,2)", "decimal(9,6)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal(ChangeFootprint.TableRewrite, alter.Footprint);
        Assert.True(plan.HasRewrite);
    }

    [Fact]
    public void RealToFloat_WidensApproximate_MetadataOnly()
    {
        var plan = PlanOne("real", "float");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("float", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
    }

    [Fact]
    public void SmallMoneyToMoney_WidensMoney_MetadataOnly()
    {
        var plan = PlanOne("smallmoney", "money");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("money", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.MetadataOnly, alter.Footprint);
    }

    [Fact]
    public void VarbinaryToMax_IsRewriteAlter()
    {
        var plan = PlanOne("varbinary(16)", "varbinary(max)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("varbinary(max)", alter.ToType.Render());
        Assert.Equal(ChangeFootprint.TableRewrite, alter.Footprint);
    }

    [Fact]
    public void DateTime2ScaleWiden_SameBase_WidensScale()
    {
        var plan = PlanOne("datetime2(3)", "datetime2(7)");
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal("datetime2(7)", alter.ToType.Render());
    }

    // ---- Type resolution: incompatible (cannot widen) edges -------------------------------------------

    [Fact]
    public void TimestampColumn_CannotWiden_IsDriftOnOrdinaryColumn()
    {
        // timestamp/rowversion is a fixed system type: WidenBinary refuses, so it surfaces as drift (not a key).
        var plan = PlanOne("timestamp", "varbinary(8)");
        Assert.False(plan.HasChanges);
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.IncompatibleOrdinaryColumn && d.Column == "C");
    }

    [Fact]
    public void DateVsDatetime2_DifferentBase_IsIncompatibleDrift()
    {
        var plan = PlanOne("date", "datetime2(3)");
        Assert.False(plan.HasChanges);
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.IncompatibleOrdinaryColumn && d.Column == "C");
    }

    [Theory]
    [InlineData("bit", "int")]
    [InlineData("uniqueidentifier", "nvarchar(36)")]
    [InlineData("int", "nvarchar(20)")]
    [InlineData("datetime2(3)", "bigint")]
    public void CrossFamilyChange_OnOrdinaryColumn_IsDrift_NotBlocked(string actual, string desired)
    {
        var plan = PlanOne(actual, desired);
        Assert.False(plan.IsBlocked);
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.IncompatibleOrdinaryColumn);
    }

    [Fact]
    public void SqlVariantOther_DifferingType_IsIncompatible()
    {
        // sql_variant maps to the Other family, which has no widening: any change is incompatible.
        var plan = PlanOne("sql_variant", "xml");
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.IncompatibleOrdinaryColumn && d.Column == "C");
    }

    // ---- Planner: criticality (key / PK / identity / hash) on incompatible change ---------------------

    [Fact]
    public void IncompatibleOnIdentityColumn_IsCritical_Blocks()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Sk", "nvarchar(10)", role: ColumnRole.Identity)],
            [EvoCol("Sk", "int")],
            EvoNoKeys);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.CriticalMismatches, m => m.Column == "Sk");
    }

    [Fact]
    public void IncompatibleOnPrimaryKeyColumn_IsCritical_Blocks()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Id", "nvarchar(10)", pk: true)],
            [EvoCol("Id", "int")],
            EvoNoKeys);
        Assert.True(plan.IsBlocked);
        Assert.Contains(plan.CriticalMismatches, m => m.Column == "Id");
    }

    [Fact]
    public void KeyColumnMatch_IsCaseInsensitive_StillCritical()
    {
        // Desired column "K" with a lowercase "k" key set entry must still be treated as a key.
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("K", "nvarchar(10)")],
            [EvoCol("K", "int")],
            EvoKeys("k"));
        Assert.True(plan.IsBlocked);
    }

    [Fact]
    public void CompatibleWidenOnKeyColumn_IsNotCritical_JustAlters()
    {
        // A key column with a widenable (same-family) change is an ordinary alter, never a critical mismatch.
        var plan = PlanOne("int", "bigint", keys: EvoKeys("C"));
        Assert.False(plan.IsBlocked);
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.Equal(ChangeFootprint.TableRewrite, alter.Footprint);
    }

    [Fact]
    public void MultipleCriticalMismatches_AllRecorded()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("A", "nvarchar(5)", pk: true), EvoCol("B", "int", role: ColumnRole.Identity)],
            [EvoCol("A", "int"), EvoCol("B", "nvarchar(5)")],
            EvoNoKeys);
        Assert.Equal(2, plan.CriticalMismatches.Count);
    }

    // ---- Planner: column-set differences --------------------------------------------------------------

    [Fact]
    public void CaseOnlyColumnNameDifference_TreatedAsExisting_NotAdded()
    {
        // actual "name", desired "Name": name lookup is case-insensitive, so the column is not re-added.
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Name", "nvarchar(50)")],
            [EvoCol("name", "nvarchar(50)")],
            EvoNoKeys);
        Assert.Empty(plan.ColumnsToAdd);
        Assert.False(plan.HasChanges);
    }

    [Fact]
    public void AddsAndAltersAndDrift_AllSurfacedTogether()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Id", "int"), EvoCol("Name", "nvarchar(100)"), EvoCol("New", "bit")],
            [EvoCol("Id", "int"), EvoCol("Name", "nvarchar(50)"), EvoCol("Legacy", "int")],
            EvoNoKeys);

        Assert.Contains(plan.ColumnsToAdd, c => c.Name == "New");
        Assert.Contains(plan.ColumnsToAlter, a => a.Name == "Name");
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.ExtraTargetColumn && d.Column == "Legacy");
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void NewColumnPreservesRequestedNullability_InPlan()
    {
        // The plan keeps the source's requested nullability; the DDL generator is what forces ADD to nullable.
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Id", "int"), EvoCol("Flag", "bit", nullable: false)],
            [EvoCol("Id", "int")],
            EvoNoKeys);
        var added = Assert.Single(plan.ColumnsToAdd);
        Assert.False(added.IsNullable);
    }

    [Fact]
    public void AlterNeverTightensNullability_EvenWhenWidening()
    {
        // Desired NOT NULL + wider type on a nullable target column: alter keeps nullable, and drift is noted.
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Amount", "bigint", nullable: false)],
            [EvoCol("Amount", "int", nullable: true)],
            EvoNoKeys);
        var alter = Assert.Single(plan.ColumnsToAlter);
        Assert.True(alter.IsNullable);
        Assert.Contains(plan.Drift, d => d.Kind == DriftKind.NullabilityNotTightened && d.Column == "Amount");
    }

    [Fact]
    public void EmptyDesired_AgainstPopulatedTarget_AllExtraColumnsAreDrift()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            Array.Empty<SqlColumn>(),
            [EvoCol("A", "int"), EvoCol("B", "nvarchar(10)")],
            EvoNoKeys);
        Assert.False(plan.HasChanges);
        Assert.Equal(2, plan.Drift.Count(d => d.Kind == DriftKind.ExtraTargetColumn));
    }

    [Fact]
    public void EmptyActualTable_IsNotCreate_AddsEveryDesiredColumn()
    {
        // A non-null but empty actual column list is an existing (empty-shaped) table, not a create.
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("A", "int"), EvoCol("B", "nvarchar(10)")],
            Array.Empty<SqlColumn>(),
            EvoNoKeys);
        Assert.False(plan.CreateTable);
        Assert.Equal(2, plan.ColumnsToAdd.Count);
    }

    [Fact]
    public void RewriteColumns_OnlyIncludesRewriteFootprint_WhenMixedWithMetadata()
    {
        var plan = SchemaEvolutionPlanner.Plan(
            [EvoCol("Big", "bigint"), EvoCol("Txt", "nvarchar(200)")],
            [EvoCol("Big", "int"), EvoCol("Txt", "nvarchar(50)")],
            EvoNoKeys);
        Assert.Equal(2, plan.ColumnsToAlter.Count);
        var rewrite = Assert.Single(plan.RewriteColumns);
        Assert.Equal("Big", rewrite.Name);
    }

    // ---- ChangeFootprintClassifier: boundary classes --------------------------------------------------

    [Theory]
    [InlineData("int", "int")]
    [InlineData("bigint", "bigint")]
    public void IntegerSameRank_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    [Theory]
    [InlineData("decimal(28,2)", "decimal(29,2)")] // class 2 -> 3
    [InlineData("decimal(9,2)", "decimal(10,2)")]  // class 0 -> 1 (boundary)
    public void DecimalCrossingHigherStorageClass_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    [Theory]
    [InlineData("decimal(20,2)", "decimal(28,2)")] // both class 2
    [InlineData("decimal(29,2)", "decimal(38,2)")] // both class 3
    public void DecimalWithinHigherStorageClass_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    [Theory]
    [InlineData("nchar(10)", "nvarchar(10)")] // fixed -> variable is not a fixed-width grow
    [InlineData("nvarchar(50)", "varchar(50)")] // unicode -> ascii, neither max nor fixed grow
    public void TextNonFixedGrowth_IsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    [Fact]
    public void TextVariableToMax_IsRewrite()
        => Assert.Equal(ChangeFootprint.TableRewrite, ChangeFootprintClassifier.Classify(SqlDataType.Parse("varchar(100)"), SqlDataType.Parse("varchar(max)")));

    [Fact]
    public void BinaryFixedToVariable_IsMetadataOnly()
        => Assert.Equal(ChangeFootprint.MetadataOnly, ChangeFootprintClassifier.Classify(SqlDataType.Parse("binary(8)"), SqlDataType.Parse("varbinary(16)")));

    [Theory]
    [InlineData("datetime2(2)", "datetime2(3)")] // class 0 -> 1 (boundary)
    [InlineData("time(4)", "time(5)")]           // class 1 -> 2 (boundary)
    public void DateTimeScaleCrossingClass_IsRewrite(string from, string to)
        => Assert.Equal(ChangeFootprint.TableRewrite, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    [Theory]
    [InlineData("real", "float")]
    [InlineData("smallmoney", "money")]
    [InlineData("uniqueidentifier", "uniqueidentifier")]
    public void NonRewritingFamilies_ClassifyAsMetadataOnly(string from, string to)
        => Assert.Equal(ChangeFootprint.MetadataOnly, ChangeFootprintClassifier.Classify(SqlDataType.Parse(from), SqlDataType.Parse(to)));

    // ---- EvolutionDdlGenerator: generation, ordering, idempotency, escaping ---------------------------

    [Fact]
    public void EmptyPlan_ProducesBatchWithNoStatements()
    {
        var batch = EvolutionDdlGenerator.Generate(EvoTarget, new EvolutionPlan(), allowTableRewrite: false);
        Assert.False(batch.HasChanges);
        Assert.Empty(batch.Statements);
        Assert.Equal("dbo", batch.Schema);
        Assert.Equal("Customer", batch.Table);
    }

    [Fact]
    public void AddStatementsPrecedeAlterStatements_InOrder()
    {
        var plan = new EvolutionPlan
        {
            ColumnsToAdd = [EvoCol("New", "int")],
            ColumnsToAlter =
            [
                new ColumnAlter
                {
                    Name = "Code",
                    FromType = SqlDataType.Parse("varchar(10)"),
                    ToType = SqlDataType.Parse("varchar(20)"),
                    IsNullable = true,
                    Footprint = ChangeFootprint.MetadataOnly,
                },
            ],
        };

        var batch = EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false);
        Assert.Equal(2, batch.Statements.Count);
        Assert.Contains("ADD [New]", batch.Statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [Code]", batch.Statements[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleAddColumns_EmitOneGuardedStatementEach_InOrder()
    {
        var plan = new EvolutionPlan { ColumnsToAdd = [EvoCol("A", "int"), EvoCol("B", "nvarchar(10)")] };
        var batch = EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false);
        Assert.Equal(2, batch.Statements.Count);
        Assert.Contains("N'A'", batch.Statements[0].Text, StringComparison.Ordinal);
        Assert.Contains("N'B'", batch.Statements[1].Text, StringComparison.Ordinal);
        Assert.All(batch.Statements, s => Assert.Equal(DdlCost.MetadataOnly, s.Cost));
    }

    [Fact]
    public void AddColumn_ForcedNullable_EvenWhenSourceNotNull()
    {
        var plan = new EvolutionPlan { ColumnsToAdd = [EvoCol("Flag", "bit", nullable: false)] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false).Statements);
        Assert.EndsWith("ADD [Flag] bit NULL;", statement.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("Select")]
    [InlineData("Group")]
    [InlineData("Primary Key")]
    public void ReservedAndSpacedColumnNames_AreBracketed_OnAdd(string columnName)
    {
        var plan = new EvolutionPlan { ColumnsToAdd = [EvoCol(columnName, "int")] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false).Statements);
        Assert.Contains($"ADD [{columnName}] int NULL;", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaWithClosingBracket_IsEscaped_InQualifiedName()
    {
        var target = RelationalObject.Parse("[Db].[od]]d].[T]");
        var plan = new EvolutionPlan { ColumnsToAdd = [EvoCol("X", "int")] };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(target, plan, allowTableRewrite: false).Statements);
        Assert.Contains("ALTER TABLE [od]]d].[T]", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTable_WithoutPrimaryKey_OmitsConstraintLine()
    {
        var plan = new EvolutionPlan
        {
            CreateTable = true,
            CreateColumns = [EvoCol("A", "int"), EvoCol("B", "nvarchar(10)")],
        };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false).Statements, s => s.IsCreateTable);
        Assert.DoesNotContain("PRIMARY KEY", statement.Text, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'[dbo].[Customer]', N'U') IS NULL", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTable_WithCompositePrimaryKey_ListsBothKeyColumns()
    {
        var plan = new EvolutionPlan
        {
            CreateTable = true,
            CreateColumns =
            [
                EvoCol("A", "int", nullable: false, pk: true),
                EvoCol("B", "int", nullable: false, pk: true),
                EvoCol("C", "nvarchar(10)"),
            ],
        };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false).Statements, s => s.IsCreateTable);
        Assert.Contains("PRIMARY KEY CLUSTERED ([A], [B])", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTablePlan_FoldsInPendingAdds_ReturnsOnlyCreate()
    {
        // When the table is being created, pending additive columns are folded into the create (the planner
        // produces a create OR adds, never both); the generator returns only the ensure-schema guard plus the
        // single CREATE statement, never a separate ADD.
        var plan = new EvolutionPlan
        {
            CreateTable = true,
            CreateColumns = [EvoCol("A", "int")],
            ColumnsToAdd = [EvoCol("Ignored", "int")],
        };

        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false).Statements, s => s.IsCreateTable);
        Assert.DoesNotContain("Ignored", statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteRefusal_NamesEveryRewriteColumn()
    {
        var plan = new EvolutionPlan
        {
            ColumnsToAlter =
            [
                new ColumnAlter { Name = "A", FromType = SqlDataType.Parse("int"), ToType = SqlDataType.Parse("bigint"), IsNullable = true, Footprint = ChangeFootprint.TableRewrite },
                new ColumnAlter { Name = "B", FromType = SqlDataType.Parse("char(8)"), ToType = SqlDataType.Parse("char(16)"), IsNullable = true, Footprint = ChangeFootprint.TableRewrite },
            ],
        };

        var ex = Assert.Throws<SchemaRewriteNotPermittedException>(() => EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false));
        Assert.Contains("A", ex.Columns);
        Assert.Contains("B", ex.Columns);
    }

    [Fact]
    public void MetadataAlterAlongsideRewrite_IsStillRefusedWithoutOptIn()
    {
        // A single rewrite among otherwise metadata-only alters still requires opt-in for the whole batch.
        var plan = new EvolutionPlan
        {
            ColumnsToAlter =
            [
                new ColumnAlter { Name = "Meta", FromType = SqlDataType.Parse("varchar(10)"), ToType = SqlDataType.Parse("varchar(20)"), IsNullable = true, Footprint = ChangeFootprint.MetadataOnly },
                new ColumnAlter { Name = "Rew", FromType = SqlDataType.Parse("int"), ToType = SqlDataType.Parse("bigint"), IsNullable = true, Footprint = ChangeFootprint.TableRewrite },
            ],
        };

        Assert.Throws<SchemaRewriteNotPermittedException>(() => EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: false));
    }

    [Fact]
    public void BlockedPlan_TakesPrecedenceOverRewriteOptIn()
    {
        // Even with rewrite opt-in, a critical mismatch blocks first with the critical-mismatch message.
        var plan = new EvolutionPlan
        {
            CriticalMismatches = [new CriticalMismatch { Column = "K", Reason = "incompatible type families" }],
            ColumnsToAlter =
            [
                new ColumnAlter { Name = "R", FromType = SqlDataType.Parse("int"), ToType = SqlDataType.Parse("bigint"), IsNullable = true, Footprint = ChangeFootprint.TableRewrite },
            ],
        };

        var ex = Assert.Throws<SqlFlowException>(() => EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: true));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RewriteAlter_PreservesNotNull_WithOptIn()
    {
        var plan = new EvolutionPlan
        {
            ColumnsToAlter =
            [
                new ColumnAlter { Name = "Id", FromType = SqlDataType.Parse("int"), ToType = SqlDataType.Parse("bigint"), IsNullable = false, Footprint = ChangeFootprint.TableRewrite },
            ],
        };
        var statement = Assert.Single(EvolutionDdlGenerator.Generate(EvoTarget, plan, allowTableRewrite: true).Statements);
        Assert.Equal(DdlCost.Rewrite, statement.Cost);
        Assert.EndsWith("ALTER COLUMN [Id] bigint NOT NULL;", statement.Text, StringComparison.Ordinal);
    }

    // ---- SqlDataType: parse/render edges that schema evolution depends on -----------------------------

    [Theory]
    [InlineData("numeric(10,2)", "decimal(10, 2)")]
    [InlineData("dec(10,2)", "decimal(10, 2)")]
    [InlineData("integer", "int")]
    [InlineData("rowversion", "timestamp")]
    public void Parse_CanonicalizesSynonyms_AndRoundTrips(string raw, string rendered)
        => Assert.Equal(rendered, SqlDataType.Parse(raw).Render());

    [Theory]
    [InlineData("  nvarchar ( 50 ) ", "nvarchar(50)")]
    [InlineData("[int]", "int")]
    [InlineData("NVARCHAR(MAX)", "nvarchar(max)")]
    [InlineData("VarChar(Max)", "varchar(max)")]
    public void Parse_IsWhitespaceBracketAndCaseTolerant(string raw, string rendered)
        => Assert.Equal(rendered, SqlDataType.Parse(raw).Render());

    [Fact]
    public void Parse_DecimalPrecisionOnly_DefaultsScaleToZero_AndRendersWithScale()
    {
        var type = SqlDataType.Parse("decimal(10)");
        Assert.Equal(10, type.Precision);
        Assert.Equal(0, type.Scale);
        Assert.Equal("decimal(10, 0)", type.Render());
    }

    [Fact]
    public void Parse_MaxLength_IsRecognizedAsMax()
    {
        var type = SqlDataType.Parse("varbinary(max)");
        Assert.True(type.IsMax);
        Assert.Equal(-1, type.Length);
    }

    [Fact]
    public void Parse_EmptyType_Throws()
        => Assert.Throws<SqlFlowException>(() => SqlDataType.Parse("   "));

    [Fact]
    public void Parse_MalformedNumericArgument_Throws()
        => Assert.Throws<SqlFlowException>(() => SqlDataType.Parse("decimal(abc,2)"));

    [Fact]
    public void IdenticalRenderedTypes_AreValueEqual_AndResolveToKeep()
    {
        var a = SqlDataType.Parse("nvarchar(50)");
        var b = SqlDataType.Parse("NVARCHAR(50)");
        Assert.Equal(a, b);
        Assert.Equal(SchemaChangeAction.Keep, SqlTypeResolution.Resolve(a, b).Action);
    }
}
