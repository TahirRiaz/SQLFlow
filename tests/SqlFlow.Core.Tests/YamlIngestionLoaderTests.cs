using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

public sealed class YamlIngestionLoaderTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    private static YamlDocumentLoader Documents()
        => new(new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(), new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(), new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

    private const string Minimal = """
        flowType: ing
        name: orders
        connections:
          src: ${env:SRC}
          dwh: ${env:DWH}
        source:
          server: src
          object: db.dbo.Orders
        target:
          server: dwh
          object: dw.raw.Orders
        load:
          keyColumns: [OrderID]
        """;

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var doc = Loader.Parse(Minimal);
        var flow = doc.Flow;

        Assert.Equal("orders", flow.SysAlias);
        Assert.True(flow.FlowId > 0);
        Assert.Equal("src", flow.Source.Server);
        Assert.Equal("Orders", flow.Source.Table.Name);
        Assert.Equal("dwh", flow.Target.Server);
        Assert.Equal("raw", flow.Target.Table.Schema);
        Assert.Equal(["OrderID"], flow.Load.KeyColumns);

        // The open-source-critical defaults: schema evolution ON, audit columns ON, streaming ON.
        Assert.True(flow.SchemaSync.Sync);
        Assert.False(flow.SchemaSync.AllowTableRewrite);
        Assert.True(flow.SystemColumns.InsertedDate);
        Assert.True(flow.SystemColumns.UpdatedDate);
        Assert.True(flow.Load.StreamData);
        Assert.Equal(7, flow.Incremental.OverlapDays);
        Assert.Equal(0, flow.Incremental.Lookback);   // no numeric rewind unless the flow asks for one
        Assert.False(flow.InitLoad.Enabled);
        Assert.Empty(flow.Assertions);
        Assert.Empty(flow.SurrogateKeys);
        Assert.Null(flow.Process.PreInvokeAlias);

        Assert.Equal(2, doc.Connections.Count);
        Assert.Contains(doc.Connections, c => c.Alias == "src" && c.ConnectionRef == "${env:SRC}");
        Assert.All(doc.Connections, c => Assert.Equal(CredentialMode.InlineConnectionString, c.Credential.Mode));
    }

    [Fact]
    public void StableFlowId_IsDeterministic()
    {
        var first = Loader.Parse(Minimal).Flow.FlowId;
        var second = Loader.Parse(Minimal).Flow.FlowId;
        Assert.Equal(first, second);
    }

    [Fact]
    public void DataSetColumn_MapsFromYaml()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
            target:
              server: dwh
              object: dw.raw.Orders
            load:
              keyColumns: [OrderID]
              dataSetColumn: SourceFile
              batchUpsert: true
            """;

        var flow = Loader.Parse(yaml).Flow;
        Assert.Equal("SourceFile", flow.Load.DataSetColumn);
        Assert.True(flow.Load.BatchUpsertToAvoidLockEscalation);
    }

    [Fact]
    public void DataSetColumn_DefaultsToNull()
        => Assert.Null(Loader.Parse(Minimal).Flow.Load.DataSetColumn);

    [Fact]
    public void ReloadColumn_MapsFromYaml()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.pre.vOrders
            target:
              server: dwh
              object: dw.arc.Orders
            load:
              keyColumns: [OrderID]
              reloadColumn: FileName_DW
            """;

        Assert.Equal("FileName_DW", Loader.Parse(yaml).Flow.Load.ReloadColumn);
    }

    [Fact]
    public void ReloadColumn_DefaultsToNull()
        => Assert.Null(Loader.Parse(Minimal).Flow.Load.ReloadColumn);

    [Fact]
    public void ReloadColumn_BlankMapsToNull()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
            target:
              server: dwh
              object: dw.raw.Orders
            load:
              keyColumns: [OrderID]
              reloadColumn: "   "
            """;

        Assert.Null(Loader.Parse(yaml).Flow.Load.ReloadColumn);
    }

    [Fact]
    public void ReloadColumn_NeedsNoKeyColumns()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.pre.vOrders
            target:
              server: dwh
              object: dw.arc.Orders
            load:
              reloadColumn: FileName_DW
            """;

        var flow = Loader.Parse(yaml).Flow;
        Assert.Equal("FileName_DW", flow.Load.ReloadColumn);
        Assert.Empty(flow.Load.KeyColumns);
    }

    [Theory]
    [InlineData("  dataSetColumn: SourceFile", "load.dataSetColumn")]
    [InlineData("  matchKeysInSourceAndTarget: true", "load.matchKeysInSourceAndTarget")]
    public void ReloadColumn_ConflictingLoadOption_Throws(string extraLoadLine, string expectedInMessage)
    {
        var yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.pre.vOrders
            target:
              server: dwh
              object: dw.arc.Orders
            load:
              keyColumns: [OrderID]
              reloadColumn: FileName_DW
            __EXTRA__
            """.Replace("__EXTRA__", extraLoadLine, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("reloadColumn", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedInMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadColumn_WithTruncateBeforeLoad_Throws()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.pre.vOrders
            target:
              server: dwh
              object: dw.arc.Orders
              truncateBeforeLoad: true
            load:
              keyColumns: [OrderID]
              reloadColumn: FileName_DW
            """;

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("truncateBeforeLoad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReloadColumn_WithScd2_Throws()
    {
        const string yaml = """
            flowType: ing
            name: orders
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.pre.vOrders
            target:
              server: dwh
              object: dw.arc.Orders
            load:
              keyColumns: [OrderID]
              reloadColumn: FileName_DW
            versioning:
              scd2:
                enabled: true
            """;

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("scd2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectConnection_SynthesizesNamedConnection()
    {
        var doc = Loader.Parse("""
            flowType: ing
            source:
              connection: ${env:SRC}
              object: db.dbo.T
            target:
              connection: Server=.;Database=dw;Integrated Security=true
              object: dw.dbo.T
            """);

        Assert.Equal("source", doc.Flow.Source.Server);
        Assert.Equal("target", doc.Flow.Target.Server);
        Assert.Contains(doc.Connections, c => c.Alias == "source" && c.ConnectionRef == "${env:SRC}");
        Assert.Contains(doc.Connections, c => c.Alias == "target" && c.ConnectionRef.Contains("Integrated Security", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FullDocument_MapsEverySection()
    {
        var doc = Loader.Parse("""
            flowType: ing
            name: full
            description: everything
            connections:
              src: ${env:SRC}
              dwh: ${env:DWH}
            source:
              server: src
              object: db.dbo.Orders
              filter: "Status = 'open'"
              filterIsAppend: false
              incrementalClause: "AND 1=1"
              ignoreColumns: [Secret1, Secret2]
              dataSetColumn: Region
            target:
              server: dwh
              object: dw.raw.Orders
              truncateBeforeLoad: true
              columnStoreIndex: true
              identityColumn: RowId
              desiredIndexes: "CREATE INDEX IX_T ON dw.raw.Orders (OrderID)"
            load:
              keyColumns: [OrderID, LineID]
              skipUpdateExisting: true
              skipInsertNew: true
              matchKeysInSourceAndTarget: true
              batchUpsert: true
              batchUpsertRowCount: 500
              streamData: false
              threads: 4
              keepStagingTable: true
              truncateStagingOnCompletion: true
              truncateSourceWhenConsolidated: true
            change:
              hashColumns: [Amount]
              hashType: SHA2_512
              ignoreColumnsInHash: [Comment]
            systemColumns:
              insertedDate: false
              updatedDate: false
              deletedDate: true
              rowStatus: true
            schema:
              sync: false
              cleanColumnNames: true
              cleanColumnNameRegex: "[^a-z]"
              replaceInvalidCharsWith: "_"
              convertUnicodeToNonUnicode: true
              allowTableRewrite: true
            incremental:
              columns: [ModifiedDate]
              dateColumn: ModifiedDate
              overlapDays: 3
              lookback: 250
              fullLoad: true
              fetchMinValuesFromSource: true
            initLoad:
              enabled: true
              fromDate: 2020-01-01
              toDate: 2024-12-31
              batchBy: M
              batchSize: 2
              keyColumn: OrderID
              keyMaxValue: 99999
            versioning:
              tokenVersioning: true
              tokenRetentionDays: 30
            preProcess: "EXEC dbo.before"
            postProcess: "EXEC dbo.after"
            virtualColumns:
              - name: LoadTag
                dataType: nvarchar(50)
                expression: "'tag'"
            assertions:
              - name: NotEmpty
                expression: SELECT COUNT(*) FROM @TableName
              - name: Fresh
                expression: SELECT 1
            surrogateKeys:
              - table: dw.dim.Customer
                column: CustomerKey
                keyColumns: [CustomerID]
                sKeyColumns: [CustId]
                server: dwh
                preProcess: "EXEC dbo.skBefore"
                postProcess: "EXEC dbo.skAfter"
            """);

        var flow = doc.Flow;
        Assert.Equal("everything", flow.Description);
        Assert.Equal("Status = 'open'", flow.Source.Filter);
        Assert.False(flow.Source.FilterIsAppend);
        Assert.Equal("AND 1=1", flow.Source.IncrementalClause);
        Assert.Equal(["Secret1", "Secret2"], flow.Source.IgnoreColumns);
        Assert.Equal("Region", flow.Source.DataSetColumn);
        Assert.True(flow.Target.TruncateBeforeLoad);
        Assert.True(flow.Target.ColumnStoreIndex);
        Assert.Equal("RowId", flow.Target.IdentityColumn);
        Assert.NotNull(flow.Target.DesiredIndexes);
        Assert.Equal(["OrderID", "LineID"], flow.Load.KeyColumns);
        Assert.True(flow.Load.SkipUpdateExisting);
        Assert.True(flow.Load.SkipInsertNew);
        Assert.True(flow.Load.MatchKeysInSourceAndTarget);
        Assert.True(flow.Load.BatchUpsertToAvoidLockEscalation);
        Assert.Equal(500, flow.Load.BatchUpsertRowCount);
        Assert.False(flow.Load.StreamData);
        Assert.Equal(4, flow.Load.Threads);
        Assert.True(flow.Load.KeepStagingTable);
        Assert.True(flow.Load.TruncatePreTableOnCompletion);
        Assert.True(flow.Load.TruncateSourceWhenConsolidated);
        Assert.Equal(["Amount"], flow.Change.HashColumns);
        Assert.Equal("SHA2_512", flow.Change.HashType);
        Assert.Equal(["Comment"], flow.Change.IgnoreColumnsInHash);
        Assert.False(flow.SystemColumns.InsertedDate);
        Assert.True(flow.SystemColumns.DeletedDate);
        Assert.True(flow.SystemColumns.RowStatus);
        Assert.False(flow.SchemaSync.Sync);
        Assert.True(flow.SchemaSync.CleanColumnNames);
        Assert.Equal("[^a-z]", flow.SchemaSync.CleanColumnNameRegex);
        Assert.Equal("_", flow.SchemaSync.ReplaceInvalidCharsWith);
        Assert.True(flow.SchemaSync.ConvertUnicodeToNonUnicode);
        Assert.True(flow.SchemaSync.AllowTableRewrite);
        Assert.Equal(["ModifiedDate"], flow.Incremental.Columns);
        Assert.Equal(3, flow.Incremental.OverlapDays);
        Assert.Equal(250, flow.Incremental.Lookback);
        Assert.True(flow.Incremental.FullLoad);
        Assert.True(flow.Incremental.FetchMinValuesFromSource);
        Assert.True(flow.InitLoad.Enabled);
        Assert.Equal(new DateOnly(2020, 1, 1), flow.InitLoad.FromDate);
        Assert.Equal(new DateOnly(2024, 12, 31), flow.InitLoad.ToDate);
        Assert.Equal("M", flow.InitLoad.BatchBy);
        Assert.Equal(2, flow.InitLoad.BatchSize);
        Assert.Equal("OrderID", flow.InitLoad.KeyColumn);
        Assert.Equal(99999, flow.InitLoad.KeyMaxValue);
        Assert.True(flow.Versioning.TokenVersioning);
        Assert.Equal(30, flow.Versioning.TokenRetentionDays);
        Assert.Equal("EXEC dbo.before", flow.Process.PreProcessOnTarget);
        Assert.Equal("EXEC dbo.after", flow.Process.PostProcessOnTarget);
        Assert.Single(flow.VirtualColumns);
        Assert.Equal("'tag'", flow.VirtualColumns[0].SelectExpression);
        Assert.Equal(["NotEmpty", "Fresh"], flow.Assertions);
        Assert.Equal(2, doc.AssertionDefinitions.Count);
        var spec = Assert.Single(flow.SurrogateKeys);
        Assert.Equal("dwh", spec.Server);
        Assert.Equal("Customer", spec.SurrogateTable.Name);
        Assert.Equal("CustomerKey", spec.SurrogateColumn);
        Assert.Equal(["CustomerID"], spec.KeyColumns);
        Assert.Equal(["CustId"], spec.SKeyColumns);
    }

    [Theory]
    [InlineData("flowType: ing\ntarget:\n  connection: x\n  object: a.b.c", "'source' is required")]
    [InlineData("flowType: ing\nsource:\n  connection: x\n  object: a.b.c", "'target' is required")]
    [InlineData("flowType: ing\nsource:\n  connection: x\ntarget:\n  connection: y\n  object: a.b.c", "source.object")]
    [InlineData("flowType: ing\nsource:\n  connection: x\n  object: onlyname\ntarget:\n  connection: y\n  object: a.b.c", "source.object")]
    [InlineData("flowType: ing\nsource:\n  object: a.b.c\ntarget:\n  connection: y\n  object: a.b.c", "needs a connection")]
    [InlineData("flowType: ing\nconnections:\n  s: x\nsource:\n  server: s\n  connection: y\n  object: a.b.c\ntarget:\n  connection: y\n  object: a.b.c", "both 'server' and 'connection'")]
    [InlineData("flowType: ing\nsource:\n  server: missing\n  object: a.b.c\ntarget:\n  connection: y\n  object: a.b.c", "not declared under 'connections:'")]
    public void InvalidDocuments_FailWithFieldPath(string yaml, string expectedFragment)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The legacy trgVersioning shorthand is accepted and maps onto the first-class temporal policy;
    /// the full surface and its guards are covered by <c>TemporalLoaderTests</c>.</summary>
    [Fact]
    public void TemporalHistory_MapsToTheTemporalPolicy()
    {
        var doc = Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            versioning: { temporalHistory: true }
            """);

        Assert.True(doc.Flow.Versioning.Temporal.Enabled);
        Assert.Equal("ver", doc.Flow.Versioning.Temporal.HistorySchema);
    }

    [Fact]
    public void InsertUnknownDimensionRow_IsRejectedAsNotImplemented()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            versioning: { insertUnknownDimensionRow: true }
            """));
        Assert.Contains("insertUnknownDimensionRow", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not yet implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertionMode_DefaultsToAuto_AndParsesManual()
    {
        var doc = Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            assertions:
              - { name: EveryRun, expression: SELECT 1 }
              - { name: Explicit, expression: SELECT 2, mode: auto }
              - { name: OnDemand, expression: SELECT 3, mode: Manual }
            """);
        Assert.Equal(["EveryRun", "Explicit", "OnDemand"], doc.Flow.Assertions);
        Assert.Equal(ExecutionMode.Auto, doc.AssertionDefinitions[0].Mode);
        Assert.Equal(ExecutionMode.Auto, doc.AssertionDefinitions[1].Mode);
        Assert.Equal(ExecutionMode.Manual, doc.AssertionDefinitions[2].Mode);
    }

    [Fact]
    public void UnknownAssertionMode_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            assertions:
              - { name: A, expression: SELECT 1, mode: sometimes }
            """));
        Assert.Contains("assertions[0].mode", ex.Message, StringComparison.Ordinal);
        Assert.Contains("auto, manual", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedHealthCheck_MapsWithDefaults_ManualMode_TargetInherited()
    {
        var doc = Loader.Parse(Minimal + """

            healthCheck:
              dateColumn: OrderDate
              baseValue: COUNT(*)
            """);

        var check = doc.HealthCheck;
        Assert.NotNull(check);
        Assert.Equal("orders_hc", check.SysAlias);
        Assert.True(check.FlowId > 0);
        Assert.Equal(ExecutionMode.Manual, check.Mode);        // embedded checks are on-demand by default
        Assert.Equal("dwh", check.Server);                     // the flow's target connection
        Assert.Equal(doc.Flow.Target.Table, check.Target);     // the flow's target table
        Assert.Equal("OrderDate", check.DateColumn);
        Assert.Equal(doc.Flow.Batch, check.Batch);
        var metric = Assert.Single(check.Metrics);
        Assert.Equal("rowCount", metric.Name);
        Assert.Equal(120, check.MaxExperimentSeconds);
        Assert.Equal(1, check.MaturityDays);
        Assert.Equal(HealthCheckTraining.Auto, check.Training);
    }

    [Fact]
    public void EmbeddedHealthCheck_ExplicitNameAndAutoMode()
    {
        var doc = Loader.Parse(Minimal + """

            healthCheck:
              name: orders-watch
              mode: auto
              dateColumn: OrderDate
              metrics:
                - name: orders
                  baseValue: COUNT(*)
                - name: revenue
                  baseValue: SUM(Amount)
            """);

        var check = doc.HealthCheck;
        Assert.NotNull(check);
        Assert.Equal("orders-watch", check.SysAlias);
        Assert.Equal(ExecutionMode.Auto, check.Mode);
        Assert.Equal(2, check.Metrics.Count);
    }

    [Theory]
    [InlineData("healthCheck:\n  baseValue: COUNT(*)", "healthCheck.dateColumn")]
    [InlineData("healthCheck:\n  dateColumn: D", "healthCheck.baseValue")]
    [InlineData("healthCheck:\n  dateColumn: D\n  baseValue: COUNT(*)\n  mode: sometimes", "healthCheck.mode")]
    [InlineData("healthCheck:\n  dateColumn: D\n  baseValue: COUNT(*)\n  ml: { maxExperimentSeconds: 0 }", "healthCheck.ml.maxExperimentSeconds")]
    [InlineData("healthCheck:\n  name: orders\n  dateColumn: D\n  baseValue: COUNT(*)", "must differ from the flow's own name")]
    public void EmbeddedHealthCheck_InvalidBlocks_FailWithTheEmbeddedFieldPath(string block, string expectedFragment)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal + "\n" + block));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedHealthCheck_RequiresAFlowName()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            healthCheck:
              dateColumn: D
              baseValue: COUNT(*)
            """));
        Assert.Contains("requires the flow to declare 'name:'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEmbeddedHealthCheck_LeavesTheDocumentNull()
        => Assert.Null(Loader.Parse(Minimal).HealthCheck);

    [Fact]
    public void DuplicateAssertionName_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            assertions:
              - { name: A, expression: SELECT 1 }
              - { name: a, expression: SELECT 2 }
            """));
        Assert.Contains("more than once", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SurrogateKeyServer_MustBeDeclared()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: ing
            source: { connection: x, object: a.b.c }
            target: { connection: y, object: a.b.c }
            surrogateKeys:
              - { table: d.dim.C, column: K, keyColumns: [Id], server: nowhere }
            """));
        Assert.Contains("surrogateKeys[0].server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesByFlowType()
    {
        var documents = Documents();

        Assert.IsType<IngestionFlowDocument>(documents.Parse(Minimal));
        Assert.IsType<FileFlowDocument>(documents.Parse("""
            name: files
            source:
              type: csv
              location: ./x.csv
            target:
              connection: ${env:DWH}
              schema: dbo
              table: X
            """));

        var ex = Assert.Throws<FlowValidationException>(() => documents.Parse("flowType: bogus\nname: x"));
        Assert.Contains("unknown flowType 'bogus'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InMemoryStores_ResolveAndOmit()
    {
        var store = InMemoryDataSourceStore.FromReferences([new KeyValuePair<string, string>("a", "${env:X}")]);
        Assert.True(store.SupportsAliases);
        Assert.Equal("${env:X}", (await store.ResolveAsync("A")).ConnectionRef);   // case-insensitive
        await Assert.ThrowsAnyAsync<SqlFlowException>(() => store.ResolveAsync("missing"));

        var assertions = new InMemoryAssertionDefinitionStore([new AssertionDefinition { Name = "N", Expression = "SELECT 1" }]);
        var resolved = await assertions.ResolveAsync(["n", "unknown"]);
        Assert.Single(resolved);
        Assert.Equal("SELECT 1", resolved["N"].Expression);
    }
}
