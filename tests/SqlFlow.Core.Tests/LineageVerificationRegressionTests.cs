using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Extraction;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Regression pins for the adversarial-verification findings (44-agent audit, 33 confirmed): every entry
/// here reproduced a real defect against the DeltaForge reference or a production-robustness rule before
/// its fix. Kept as one suite so the findings stay fixed.
/// </summary>
public sealed class LineageVerificationRegressionTests
{
    private static ScriptDependencies Extract(string sql, string? db = null) => TSqlLineageExtractor.Extract(sql, "test", db);

    private static List<LineageFact> Facts(ScriptDependencies deps, int minimumParts = 2)
        => ScriptFactBuilder.Facts(deps, "flow", null, "@srv", LineageTier.Observed, minimumParts).ToList();

    // ---- The lifecycle findings (critical/major fidelity) ------------------------------------------------

    [Fact]
    public void FullRefresh_DropCreateInsert_IsWritesOnly_AndSurvivesTheStagingFilter()
    {
        // The canonical rebuild loader: in the reference this is the Refreshed lifecycle: Writes ONLY (the
        // schema re-definition is incidental), and it must NOT be erased as staging.
        var deps = Extract("""
            DROP TABLE IF EXISTS DW.dbo.X;
            CREATE TABLE DW.dbo.X (Id int);
            INSERT INTO DW.dbo.X SELECT Id FROM SRC.dbo.S;
            """);

        var relations = Facts(deps).Where(f => f.Name == "X").Select(f => f.Relation).OrderBy(r => r).ToList();
        Assert.Equal(new[] { LineageRelation.Writes, LineageRelation.Requires }.OrderBy(r => r), relations);
        Assert.Contains(Facts(deps), f => f.Relation == LineageRelation.Reads && f.Name == "S");
    }

    [Fact]
    public void IdempotentDdl_DropThenCreate_NoData_IsTheCreator()
    {
        var deps = Extract("""
            DROP TABLE IF EXISTS DW.dbo.X;
            CREATE TABLE DW.dbo.X (Id int);
            """);

        var relations = Facts(deps).Where(f => f.Name == "X").Select(f => f.Relation).ToList();
        Assert.Equal([LineageRelation.Creates], relations);
    }

    [Fact]
    public void InsertThenDrop_IsDestroysOnly_NoManufacturedWriterRole()
    {
        // The reference's Destroyed lifecycle: even though the script inserted, drop-without-create means
        // Destroys ONLY; an extra Writes here manufactured a mutual cycle with readers.
        var deps = Extract("""
            INSERT INTO DW.dbo.X SELECT Id FROM SRC.dbo.S;
            DROP TABLE DW.dbo.X;
            """);

        var relations = Facts(deps).Where(f => f.Name == "X").Select(f => f.Relation).ToList();
        Assert.Equal([LineageRelation.Destroys], relations);
    }

    [Fact]
    public void CreatePlusInsert_IsCreatorAndWriter()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.X (Id int);
            INSERT INTO DW.dbo.X SELECT Id FROM SRC.dbo.S;
            """);

        var relations = Facts(deps).Where(f => f.Name == "X").Select(f => f.Relation).OrderBy(r => r).ToList();
        Assert.Equal(new[] { LineageRelation.Writes, LineageRelation.Creates }.OrderBy(r => r), relations);
    }

    [Fact]
    public void WriteWithoutCreate_DerivesTheRequiresPrerequisite()
    {
        // A pure loader requires its target to exist, so the scheduler waits for whoever creates it.
        var deps = Extract("INSERT INTO DW.dbo.Orders SELECT * FROM SRC.dbo.Orders;");

        var target = Facts(deps).Where(f => f.Database == "DW").Select(f => f.Relation).OrderBy(r => r).ToList();
        Assert.Contains(LineageRelation.Requires, target);
        Assert.Contains(LineageRelation.Writes, target);
    }

    [Fact]
    public void SelfValidationRead_IsPruned()
    {
        // Read-after-own-write (the validation pattern) must not become a Reads relation.
        var deps = Extract("""
            INSERT INTO DW.dbo.X SELECT * FROM SRC.dbo.S;
            SELECT COUNT(*) FROM DW.dbo.X;
            """);

        Assert.DoesNotContain(Facts(deps), f => f.Relation == LineageRelation.Reads && f.Name == "X");
    }

    [Fact]
    public void EngineStaging_CreateBeforeDrop_StillDissolves()
    {
        var deps = Extract("""
            CREATE TABLE DW.dbo.stg_1 (Id int);
            INSERT INTO DW.dbo.stg_1 SELECT Id FROM SRC.dbo.S;
            INSERT INTO DW.dbo.Final SELECT Id FROM DW.dbo.stg_1;
            DROP TABLE DW.dbo.stg_1;
            """);

        Assert.DoesNotContain(Facts(deps), f => f.Name == "stg_1");
        Assert.Contains(Facts(deps), f => f.Relation == LineageRelation.Writes && f.Name == "Final");
    }

    // ---- The extractor adversary findings -----------------------------------------------------------------

    [Fact]
    public void DdlTrigger_DoesNotCrash_AndItsBodyExtracts()
    {
        var deps = Extract("""
            CREATE TRIGGER trg_Audit ON DATABASE FOR CREATE_TABLE AS
            BEGIN
                INSERT INTO DW.audit.DdlLog (At) SELECT GETUTCDATE();
            END
            """);

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.audit.ddllog");
    }

    [Fact]
    public void CreateOrAlterView_IsViewLineage()
    {
        var deps = Extract("CREATE OR ALTER VIEW dbo.vw_X AS SELECT * FROM dbo.Base1;", db: "DW");

        Assert.Contains(deps.Outbound, d => d.Table.Key == "dw.dbo.vw_x");
        Assert.Contains(deps.Inbound, d => d.Table.Key == "dw.dbo.base1");
        Assert.Equal(["dw.dbo.base1"], deps.LocalDeps["dw.dbo.vw_x"]);
    }

    [Fact]
    public void SynapseCtas_BodyIsRealLineage()
    {
        var deps = Extract("CREATE TABLE DW.dbo.Mart WITH (DISTRIBUTION = HASH(Id)) AS SELECT Id FROM DW.dbo.Fact1;");

        Assert.Contains(deps.CtasCreated, k => k == "dw.dbo.mart");
        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.fact1" && p.Target.Key == "dw.dbo.mart");
    }

    [Fact]
    public void CursorDefinition_KeepsItsSelectLineage_Quietly()
    {
        var deps = Extract("""
            DECLARE c CURSOR FOR SELECT Id FROM DW.dbo.Orders;
            OPEN c;
            FETCH NEXT FROM c;
            CLOSE c;
            DEALLOCATE c;
            """);

        Assert.Contains(deps.Inbound, d => d.Table.Key == "dw.dbo.orders");
        Assert.Empty(deps.Warnings);
    }

    [Fact]
    public void SelectVariableAssignment_WalksItsSubqueries()
    {
        var deps = Extract("SELECT @x = (SELECT MAX(Id) FROM DW.dbo.Watermarks) FROM DW.dbo.Anchor;");

        Assert.Contains(deps.Inbound, d => d.Table.Key == "dw.dbo.watermarks" && d.Kind == DependencyKind.Subquery);
    }

    [Fact]
    public void PartitionSwitch_IsADataMovement()
    {
        var deps = Extract("ALTER TABLE DW.dbo.Staging SWITCH PARTITION 3 TO DW.dbo.Fact1 PARTITION 3;");

        Assert.Contains(deps.DataFlowPairs, p => p.Source.Key == "dw.dbo.staging" && p.Target.Key == "dw.dbo.fact1");
        Assert.Contains(Facts(deps), f => f.Relation == LineageRelation.Writes && f.Name == "Fact1");
    }

    [Fact]
    public void DmlThroughCteAlsoNamedInFrom_WarnsInsteadOfPhantomWrite()
    {
        var deps = Extract("""
            WITH cte AS (SELECT * FROM DW.dbo.Orders)
            UPDATE cte SET Amount = 0 FROM cte;
            """);

        Assert.DoesNotContain(deps.Outbound, d => d.Table.Name.Equals("cte", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deps.Warnings, w => w.Contains("DML through CTE", StringComparison.Ordinal));
    }

    [Fact]
    public void DmlTargetingADerivedTableAlias_Warns()
    {
        var deps = Extract("UPDATE d SET Amount = 0 FROM (SELECT * FROM DW.dbo.Orders) d;");

        Assert.Empty(deps.Outbound);
        Assert.Contains(deps.Warnings, w => w.Contains("aliases a derived table", StringComparison.Ordinal));
    }

    [Fact]
    public void StringSplitAndOpenJson_AreQuiet_NonLineage()
    {
        var deps = Extract("""
            SELECT s.value FROM STRING_SPLIT(@csv, ',') s;
            SELECT j.* FROM OPENJSON(@doc) j;
            """);

        Assert.Empty(deps.Warnings);
        Assert.Empty(deps.Inbound);
    }

    [Fact]
    public void TopAndOrderBy_SubqueriesAreRead()
    {
        // OFFSET/FETCH grammatically take constants or variables only; TOP and ORDER BY expressions can
        // carry subqueries and must be walked.
        var deps = Extract("""
            SELECT TOP ((SELECT MAX(N) FROM DW.dbo.Limits)) Id
            FROM DW.dbo.Orders
            ORDER BY CASE WHEN EXISTS (SELECT 1 FROM DW.dbo.SortHints) THEN 1 ELSE 2 END;
            """);

        Assert.Empty(deps.Warnings);
        var keys = deps.Inbound.Select(d => d.Table.Key).ToList();
        Assert.Contains("dw.dbo.limits", keys);
        Assert.Contains("dw.dbo.sorthints", keys);
    }

    [Fact]
    public void UseQualifiedOnePartName_CannotCollideWithSchemaQualified()
    {
        var deps = Extract("""
            USE DW;
            SELECT * FROM Orders;
            SELECT * FROM Orders.Lines;
            """);

        // 'DW..orders' (db-qualified, no schema) and 'orders.lines' (schema-qualified) are distinct keys.
        var keys = deps.Inbound.Select(d => d.Table.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(["dw..orders", "dw.orders.lines"], keys);
    }

    [Fact]
    public void EmptyCte_LeavesNoLocalDepsEntry()
    {
        var deps = Extract("""
            WITH seed AS (SELECT 1 AS N)
            INSERT INTO DW.dbo.T SELECT N FROM seed;
            """);

        Assert.False(deps.LocalDeps.ContainsKey("seed"));
        Assert.Contains(Facts(deps), f => f.Relation == LineageRelation.Writes && f.Name == "T");
    }

    // ---- Robustness findings --------------------------------------------------------------------------------

    [Fact]
    public void ServerIdentity_HybridReferenceLiteral_Hashes_NeverEchoes()
    {
        // A literal that merely STARTS with ${...} is still a literal carrying a secret.
        var hybrid = ServerIdentity.From("${env:HOST};Database=DW;Password=hunter2");

        Assert.StartsWith("inline:", hybrid, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter", hybrid, StringComparison.OrdinalIgnoreCase);

        // Whole references still pass through.
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", ServerIdentity.From("${env:SQLFLOW_CONN_DWH}"));
        Assert.Equal("@alias", ServerIdentity.From("@alias"));
    }

    [Fact]
    public void RedactedMessage_ScrubsEverySecretFragment()
    {
        var message = "Login failed for 'Server=x;User ID=u;Password=hunter2;Encrypt=True' and pwd=abc at the end";

        var redacted = SecretHygiene.RedactedMessage(message);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("pwd=abc", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Password=[redacted]", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Encrypt=True", redacted, StringComparison.Ordinal); // non-secrets survive
    }

    [Fact]
    public async Task DuplicateFlowNames_DoNotCrashTheObservedTier()
    {
        using var estate = new LineageEstateHarness()
            .Flow("a/dup.flow.yaml", LineageEstateHarness.Ingestion("dup-flow", "S.dbo.A", "DW.dbo.B"))
            .Flow("b/dup.flow.yaml", LineageEstateHarness.Ingestion("dup-flow", "S.dbo.A", "DW.dbo.B"))
            .Run("dup-flow", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[B] SELECT 1;"));

        var report = await estate.ComputeAsync();

        Assert.Contains(report.Warnings, w => w.Contains("declared by 2 documents", StringComparison.Ordinal));
        Assert.Single(report.Flows, f => f.Name == "dup-flow");
    }

    [Fact]
    public async Task RunHistoryNextToASubdirectoryDocument_IsFound()
    {
        using var estate = new LineageEstateHarness()
            .Flow("nested/deep/load.flow.yaml", LineageEstateHarness.Ingestion("nested-load", "S.dbo.A", "DW.dbo.B"));

        // The engine writes run history next to the DOCUMENT, not the estate root.
        var documentDirectory = Path.Combine(estate.Root, "nested", "deep");
        var folder = Path.Combine(documentDirectory, ".sqlflow", "runs", "nested-load", "20260610-060000_aaaa1111");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), """
            { "schemaVersion": 1, "flowKind": "ing", "flowName": "nested-load",
              "runId": "7e4f4d3b-1111-2222-3333-444455556666", "success": true,
              "writtenUtc": "2026-06-10T06:00:00Z",
              "result": { "sqlTrace": [ { "sequence": 1, "step": "postprocess", "sql": "INSERT INTO [DW].[audit].[Log1] (At) SELECT GETUTCDATE();" } ] } }
            """);

        var report = await estate.ComputeAsync();

        Assert.Contains(report.Edges, e => e.Tier == LineageTier.Observed && e.ObjectKey.EndsWith("|dw|audit|log1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidJsonWrongShape_RunArtifact_WarnsInsteadOfCrashing()
    {
        using var estate = new LineageEstateHarness()
            .Flow("load.flow.yaml", LineageEstateHarness.Ingestion("load-x", "S.dbo.A", "DW.dbo.B"));

        var folder = Path.Combine(estate.Root, ".sqlflow", "runs", "load-x", "20260610-060000_aaaa1111");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "run.json"), """{ "runId": ["not", "a", "guid"], "result": 42 }""");

        var report = await estate.ComputeAsync();

        Assert.NotNull(report); // no crash; the artifact degraded to nothing or a warning
    }

    [Fact]
    public async Task FileEndpointIdentity_NormalizesEquivalentSpellings()
    {
        using var estate = new LineageEstateHarness()
            .Flow("a.flow.yaml", """
                name: csv-a
                source: { type: csv, location: ./data/orders.csv }
                target:
                  connection: ${env:SQLFLOW_CONN_DWH}
                  schema: raw
                  table: A
                """)
            .Flow("b.flow.yaml", """
                name: csv-b
                source: { type: csv, location: data/orders.csv }
                target:
                  connection: ${env:SQLFLOW_CONN_DWH}
                  schema: raw
                  table: B
                """);

        var report = await estate.ComputeAsync();

        Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File);
    }

    // ---- Graph findings --------------------------------------------------------------------------------------

    [Fact]
    public void UnifiedNode_MetadataAgreesWithItsKey()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(new CollectedFlow
        {
            Node = new LineageFlowNode { Name = "f", Kind = "file", File = "f.flow.yaml", Batch = null },
            TargetServerRef = "@dwh",
            FileWriteUtc = DateTime.UnixEpoch,
        });
        collected.Facts.Add(new LineageFact
        {
            Flow = "f", Relation = LineageRelation.Writes, ServerRef = "@dwh",
            Database = null, Schema = "dbo", Name = "Orders", Tier = LineageTier.Declared,
        });
        collected.Facts.Add(new LineageFact
        {
            Flow = "f", Relation = LineageRelation.Reads, ServerRef = "@dwh",
            Database = "DW", Schema = "dbo", Name = "Orders", Tier = LineageTier.Observed,
        });

        var report = Graph.UnifiedBuild(collected);

        var node = Assert.Single(report.Objects, o => o.Name == "Orders");
        Assert.Equal("DW", node.Database, ignoreCase: true);
        Assert.EndsWith("|dw|dbo|orders", node.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void PartiallyQualifiedSynonymReference_StillResolvesThroughTheSynonym()
    {
        var collected = new CollectionResult();
        collected.Flows.Add(new CollectedFlow
        {
            Node = new LineageFlowNode { Name = "writer", Kind = "ing", File = "w.flow.yaml", Batch = null },
            TargetServerRef = "@dwh",
            FileWriteUtc = DateTime.UnixEpoch,
        });
        collected.Flows.Add(new CollectedFlow
        {
            Node = new LineageFlowNode { Name = "reader", Kind = "hc", File = "r.flow.yaml", Batch = null },
            TargetServerRef = "@dwh",
            FileWriteUtc = DateTime.UnixEpoch,
        });
        collected.Facts.Add(new LineageFact
        {
            Flow = "writer", Relation = LineageRelation.Writes, ServerRef = "@dwh",
            Database = "DW", Schema = "dbo", Name = "Orders", Tier = LineageTier.Declared,
        });

        // The reader references the synonym WITHOUT its database; unification must fill it before the
        // synonym map lookup, or this edge silently vanishes.
        collected.Facts.Add(new LineageFact
        {
            Flow = "reader", Relation = LineageRelation.Reads, ServerRef = "@dwh",
            Database = null, Schema = "dbo", Name = "syn_Orders", Tier = LineageTier.Declared,
        });
        collected.CatalogObjects.Add(new CatalogObject
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_Orders", Kind = LineageNodeKind.Synonym,
        });
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = "@dwh", Database = "DW", Schema = "dbo", Name = "syn_Orders",
            TargetDatabase = "DW", TargetSchema = "dbo", TargetName = "Orders",
        });

        var report = Graph.UnifiedBuild(collected);

        Assert.Equal([["writer"], ["reader"]], report.ExecutionPlan.Waves.Select(w => w.Flows).ToList());
    }

    private static class Graph
    {
        public static LineageReport UnifiedBuild(CollectionResult collected)
            => SqlFlow.Lineage.Graph.LineageGraphBuilder.Build(
                collected, "flows", [LineageTier.Declared], new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc));
    }
}
