using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.SchemaRegistration;
using SqlFlow.Core.Subscribers;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.SchemaRegistration;

/// <summary>
/// The schema registration flow (flowType: sch) end to end without a database: the document it accepts, the pipeline
/// row it projects (a declared datasource, outside every wave), what the collector records for it, and how the graph
/// builder attributes registered objects to it and links a registered-source subscriber's reads onto them.
/// </summary>
public sealed class SchemaRegistrationFlowTests : IDisposable
{
    private const string RegistrationYaml = """
        flowType: sch
        name: adventureworks_00_sch
        batch: sch
        description: AdventureWorks tables and views
        connections:
          aw: ${env:SQLFLOW_ADVENTUREWORKS_DB}
        source:
          server: aw
          database: AdventureWorks
        objects:
          includeSchemas: [dbo, "[sales]"]
          excludeSchemas: [staging]
        schedule:
          cron: "0 4 * * *"
          timezone: Europe/Oslo
        """;

    private const string ServerRef = "${env:SQLFLOW_ADVENTUREWORKS_DB}";

    private static readonly DateTime GeneratedAt = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-sch-" + Guid.NewGuid().ToString("N")[..8]);

    public SchemaRegistrationFlowTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static YamlDocumentLoader Documents() => new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
        new YamlTranslateFlowLoader(), new YamlSchemaRegistrationFlowLoader());

    // ---- The document -------------------------------------------------------------------------------------

    [Fact]
    public void Parse_MapsTheWholeDocument()
    {
        var document = new YamlSchemaRegistrationFlowLoader().Parse(RegistrationYaml);
        var flow = document.Flow;

        Assert.Equal("adventureworks_00_sch", flow.SysAlias);
        Assert.Equal("sch", flow.Batch);
        Assert.Equal("AdventureWorks tables and views", flow.Description);
        Assert.Equal("aw", flow.Server);
        Assert.Equal("@aw", flow.ConnectionReference);
        Assert.Equal("AdventureWorks", flow.Scope.Database);
        Assert.Equal(["dbo", "sales"], flow.Scope.IncludeSchemas);
        Assert.Equal(["staging"], flow.Scope.ExcludeSchemas);
        Assert.Equal(ServerRef, Assert.Single(document.Connections).ConnectionRef);
    }

    [Fact]
    public void Parse_WithoutObjects_RegistersEverySchemaOfTheConnectionsDatabase()
    {
        var flow = new YamlSchemaRegistrationFlowLoader().Parse("""
            flowType: sch
            name: minimal_00_sch
            source:
              connection: ${env:SQLFLOW_MINIMAL_DB}
            """).Flow;

        Assert.Null(flow.Scope.Database);
        Assert.Empty(flow.Scope.IncludeSchemas);
        Assert.Empty(flow.Scope.ExcludeSchemas);
        Assert.True(flow.Scope.Includes("anything"));
    }

    [Theory]
    [InlineData("""
        flowType: sch
        name: x_00_sch
        connections:
          aw: ${env:A}
        """, "'source' is required")]
    [InlineData("""
        flowType: sch
        connections:
          aw: ${env:A}
        source:
          server: aw
        """, "'name' is required")]
    [InlineData("""
        flowType: sch
        name: x_00_sch
        connections:
          aw: ${env:A}
        source:
          server: aw
        objects:
          includeSchemas: [dbo]
          excludeSchemas: [DBO]
        """, "cannot be both registered and skipped")]
    [InlineData("""
        flowType: sch
        name: x_00_sch
        connections:
          pg:
            connection: ${env:PG}
            provider: postgres
        source:
          server: pg
        """, "source")]
    public void Parse_RejectsAnInvalidDocument(string yaml, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => new YamlSchemaRegistrationFlowLoader().Parse(yaml));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesOnFlowType()
    {
        var document = Documents().Parse(RegistrationYaml);

        var registration = Assert.IsType<SchemaRegistrationFlowDocument>(document);
        Assert.Equal("adventureworks_00_sch", registration.Document.Flow.SysAlias);
        Assert.Equal("0 4 * * *", registration.Schedule!.Cron);
    }

    [Fact]
    public void Scope_ComparesSchemasCaseInsensitively_AndAppliesExcludeAfterInclude()
    {
        var scope = new SchemaRegistrationScope { IncludeSchemas = ["dbo", "Sales"], ExcludeSchemas = ["sales"] };

        Assert.True(scope.Includes("DBO"));
        Assert.False(scope.Includes("sales"));
        Assert.False(scope.Includes("hr"));
    }

    // ---- The pipeline and the collector ---------------------------------------------------------------------

    [Fact]
    public void Header_IsADeclaredDatasource_OutsideLineageWaves()
    {
        var header = Assert.Single(FlowDocumentHeaders.Project(Documents().Parse(RegistrationYaml)));

        Assert.Equal("adventureworks_00_sch", header.Name);
        Assert.Equal("sch", header.Kind);
        Assert.Equal(ServerRef, header.SourceServerRef);
        Assert.Equal(ServerIdentity.FileSystem, header.TargetServerRef);
        Assert.False(header.ParticipatesInLineage);
        Assert.Equal("0 4 * * *", header.Schedule!.Cron);
    }

    [Fact]
    public void Collector_RecordsTheRegistration_WithoutAFactOrADerivedServer()
    {
        Write("adventureworks_00_sch.yaml", RegistrationYaml);

        var collected = new FlowSetCollector().Collect(_root);

        var registration = Assert.Single(collected.SchemaRegistrations);
        Assert.Equal("adventureworks_00_sch", registration.Flow);
        Assert.Equal(ServerRef, registration.ServerRef);
        Assert.Equal(ServerRef, registration.RawReference);
        Assert.Equal("AdventureWorks", registration.Scope.Database);

        // The general derived tier would parse modules and infer joins; a registration takes part in neither.
        Assert.Empty(collected.Servers);
        Assert.Empty(collected.Facts);
        Assert.Single(collected.Flows, f => f.Node.Kind == "sch" && !f.ParticipatesInLineage);
    }

    [Fact]
    public void SubscriberLibrary_WithoutConnections_IsARegisteredSource()
    {
        var library = new YamlSubscriberLibraryLoader().Parse("""
            subscribers:
              Sales_Report:
                type: PowerBI
                queries:
                  - name: Orders
                    sql: SELECT OrderId FROM AdventureWorks.dbo.FactResellerSales
            """, "subscribers.yaml");

        var subscriber = Assert.Single(library.Subscribers);
        Assert.True(subscriber.IsRegisteredSource);
        Assert.Equal(DataSubscriber.RegisteredSource, Assert.Single(subscriber.Queries).Server);
        Assert.DoesNotContain("Sales_Report", library.UnlinkedWarnings.Keys);
    }

    [Fact]
    public void Collector_KeysARegisteredSourceSubscribersReads_OnTheRegisteredIdentity()
    {
        Write("subscribers.yaml", """
            subscribers:
              Sales_Report:
                type: PowerBI
                queries:
                  - name: Orders
                    sql: SELECT OrderId FROM AdventureWorks.dbo.FactResellerSales
            """);

        var collected = new FlowSetCollector().Collect(_root);

        var fact = Assert.Single(collected.Facts);
        Assert.Equal(ServerIdentity.Registered, fact.ServerRef);
        Assert.Equal("AdventureWorks", fact.Database);
        Assert.Equal("dbo", fact.Schema);
        Assert.Equal("FactResellerSales", fact.Name);
        Assert.Empty(collected.Servers);
    }

    // ---- The graph ----------------------------------------------------------------------------------------

    private static CollectionResult Registered(string flow, string server, string database, params string[] tables)
    {
        var collected = new CollectionResult();
        collected.SchemaRegistrations.Add(new CollectedSchemaRegistration
        {
            Flow = flow,
            ServerRef = server,
            RawReference = server,
            Kind = Core.Connections.DataSourceKind.MSSQL,
            Scope = new SchemaRegistrationScope(),
        });
        SchemaRegistrationCollector.Add(
            collected, flow, server, database,
            tables.Select(t => new RegisteredObject
            {
                Schema = "dbo",
                Name = t,
                Kind = LineageNodeKind.Table,
                Columns = [new LineageColumn { Ordinal = 1, Name = "Id", DataType = "int" }],
                KeyColumns = ["Id"],
                Script = $"CREATE TABLE [dbo].[{t}] (\n    [Id] int NOT NULL\n);",
            }).ToList());
        return collected;
    }

    private static void AddRegisteredReader(
        CollectionResult collected, string subscriber, string? database, string schema, string name)
    {
        var key = NodeKey.For(ServerIdentity.Subscriber, null, null, subscriber);
        collected.Facts.Add(new LineageFact
        {
            ViaModuleKey = key,
            Relation = LineageRelation.Reads,
            ServerRef = ServerIdentity.Registered,
            Database = database,
            Schema = schema,
            Name = name,
            Tier = LineageTier.Declared,
        });
        collected.Subscribers.Add(new CollectedSubscriber
        {
            Subscriber = new DataSubscriber { Name = subscriber, Type = "PowerBI", IsRegisteredSource = true },
            NodeKey = key,
            File = "subscribers.yaml",
            Queries =
            [
                new CollectedSubscriberQuery
                {
                    Name = "q",
                    ServerRef = ServerIdentity.Registered,
                    Sql = "SELECT 1",
                    Objects = [new ModelObjectRef { ServerRef = ServerIdentity.Registered, Database = database, Schema = schema, Name = name }],
                },
            ],
        });
    }

    private static LineageReport Build(CollectionResult collected)
        => LineageGraphBuilder.Build(collected, "flows", [LineageTier.Declared, LineageTier.Derived], GeneratedAt);

    [Fact]
    public void Builder_EmitsARegistersEdge_PerRegisteredObject_WithItsScriptAndKey()
    {
        var report = Build(Registered("aw_00_sch", ServerRef, "AdventureWorks", "DimDate", "FactResellerSales"));

        var registers = report.Edges.Where(e => e.Relation == LineageRelation.Registers).ToList();
        Assert.Equal(2, registers.Count);
        Assert.All(registers, e =>
        {
            Assert.Equal("aw_00_sch", e.Flow);
            Assert.Null(e.ViaModule);
            Assert.Equal(LineageTier.Derived, e.Tier);
        });

        var node = Assert.Single(report.Objects, o => o.Name == "FactResellerSales");
        Assert.Equal(LineageNodeKind.Table, node.Kind);
        Assert.Equal(["Id"], node.KeyColumns);
        Assert.Equal(LineageModelOrigin.Constraint, node.KeyOrigin);
        Assert.StartsWith("CREATE TABLE [dbo].[FactResellerSales]", node.Script, StringComparison.Ordinal);
        Assert.Single(node.Columns);

        // Metadata only: a registration orders nothing.
        Assert.Empty(report.ExecutionPlan.Waves);
        Assert.Empty(report.FlowDependencies);
    }

    [Fact]
    public void Builder_LinksAThreePartRead_OntoTheRegisteredObject()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        AddRegisteredReader(collected, "Sales_Report", "AdventureWorks", "dbo", "FactResellerSales");

        var report = Build(collected);

        var registeredKey = NodeKey.For(ServerRef, "AdventureWorks", "dbo", "FactResellerSales");
        var read = Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads);
        Assert.Equal(registeredKey, read.ObjectKey);
        Assert.DoesNotContain(report.Objects, o => o.ServerRef == ServerIdentity.Registered);

        var query = Assert.Single(Assert.Single(report.Subscribers).Queries);
        Assert.Equal(ServerRef, query.ServerRef);
        Assert.Equal([registeredKey], query.ObjectKeys);
    }

    [Fact]
    public void Builder_LinksATwoPartRead_WhenOneRegisteredDatabaseHasIt()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        AddRegisteredReader(collected, "Sales_Report", null, "dbo", "FactResellerSales");

        var report = Build(collected);

        Assert.Equal(
            NodeKey.For(ServerRef, "AdventureWorks", "dbo", "FactResellerSales"),
            Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads).ObjectKey);
    }

    [Fact]
    public void Builder_LeavesAnUnregisteredRead_UnlinkedAndSaysSo()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        AddRegisteredReader(collected, "Sales_Report", "Other", "dbo", "Orders");

        var report = Build(collected);

        var read = Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads);
        Assert.Equal(NodeKey.For(ServerIdentity.Registered, "Other", "dbo", "Orders"), read.ObjectKey);
        Assert.Contains(report.Warnings, w => w.Contains("no registered database", StringComparison.Ordinal)
            && w.Contains("Other.dbo.Orders", StringComparison.Ordinal));
        Assert.Equal(ServerIdentity.Registered, Assert.Single(Assert.Single(report.Subscribers).Queries).ServerRef);
    }

    [Fact]
    public void Builder_RefusesToGuess_BetweenTwoRegisteredDatabases()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "Orders");
        collected.Merge(Registered("crm_00_sch", "${env:SQLFLOW_CRM_DB}", "Crm", "Orders"));
        AddRegisteredReader(collected, "Sales_Report", null, "dbo", "Orders");

        var report = Build(collected);

        var read = Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads);
        Assert.StartsWith(ServerIdentity.Registered + "|", read.ObjectKey, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, w => w.Contains("aw_00_sch", StringComparison.Ordinal)
            && w.Contains("crm_00_sch", StringComparison.Ordinal));
    }

    [Fact]
    public void Builder_ResolvesAModelTable_ThroughItsSynonym_ThenTheRegistration()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        var key = NodeKey.For(ServerIdentity.Subscriber, null, null, "Sales_Report");
        collected.Synonyms.Add(new SynonymLink
        {
            ServerRef = ServerIdentity.Registered,
            Database = string.Empty,
            Schema = string.Empty,
            Name = "Sales",
            TargetDatabase = "AdventureWorks",
            TargetSchema = "dbo",
            TargetName = "FactResellerSales",
        });
        collected.Facts.Add(new LineageFact
        {
            ViaModuleKey = key,
            Relation = LineageRelation.Reads,
            ServerRef = ServerIdentity.Registered,
            Name = "Sales",
            Tier = LineageTier.Declared,
        });
        collected.Subscribers.Add(new CollectedSubscriber
        {
            Subscriber = new DataSubscriber { Name = "Sales_Report", Type = "PowerBI", IsRegisteredSource = true },
            NodeKey = key,
            File = "subscribers.yaml",
            Queries = [],
            Models =
            [
                new LineageSubscriberModel
                {
                    ReportFile = "Sales.pbix",
                    Tables =
                    [
                        new LineageSubscriberModelTable
                        {
                            Name = "Sales",
                            SourceDatabase = "AdventureWorks",
                            SourceSchema = "dbo",
                            SourceName = "FactResellerSales",
                            ServerRef = ServerIdentity.Registered,
                            Fields = [],
                        },
                    ],
                    Relationships = [],
                },
            ],
        });

        var report = Build(collected);

        var registeredKey = NodeKey.For(ServerRef, "AdventureWorks", "dbo", "FactResellerSales");
        Assert.Equal(registeredKey, Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads).ObjectKey);
        var table = Assert.Single(Assert.Single(Assert.Single(report.Subscribers).Models).Tables);
        Assert.Equal(registeredKey, table.ObjectKey);
    }

    [Fact]
    public void Builder_UsesRememberedRegistrations_AndDropsTheCurrentReposUndeclaredOnes()
    {
        var collected = new CollectionResult();
        collected.PriorRegistrations.Add(new CollectedRegisteredObject
        {
            Flow = "other_repo_00_sch", ServerRef = ServerRef, Database = "AdventureWorks", Schema = "dbo", Name = "DimDate",
        });
        collected.PriorRegistrations.Add(new CollectedRegisteredObject
        {
            Flow = "removed_00_sch", ServerRef = "${env:SQLFLOW_OLD_DB}", Database = "Old", Schema = "dbo", Name = "Orders",
            FromCurrentRepo = true,
        });
        AddRegisteredReader(collected, "Dates", null, "dbo", "DimDate");
        AddRegisteredReader(collected, "Orders", null, "dbo", "Orders");

        var report = Build(collected);

        var reads = report.Edges.Where(e => e.Relation == LineageRelation.Reads).Select(e => e.ObjectKey).ToList();
        Assert.Contains(NodeKey.For(ServerRef, "AdventureWorks", "dbo", "DimDate"), reads);
        Assert.Contains(NodeKey.For(ServerIdentity.Registered, null, "dbo", "Orders"), reads);

        // Remembered registrations are resolution evidence only; this build registers nothing itself.
        Assert.DoesNotContain(report.Edges, e => e.Relation == LineageRelation.Registers);
    }

    [Fact]
    public void Builder_SupersedesTheCurrentReposRememberedRegistration_WithItsFreshHarvest()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        collected.PriorRegistrations.Add(new CollectedRegisteredObject
        {
            Flow = "aw_00_sch", ServerRef = ServerRef, Database = "AdventureWorks", Schema = "dbo", Name = "DroppedTable",
            FromCurrentRepo = true,
        });
        AddRegisteredReader(collected, "Old_Report", null, "dbo", "DroppedTable");

        var report = Build(collected);

        Assert.Equal(
            NodeKey.For(ServerIdentity.Registered, null, "dbo", "DroppedTable"),
            Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads).ObjectKey);
    }

    [Fact]
    public void Builder_LeavesARegularSubscribersReads_Untouched()
    {
        var collected = Registered("aw_00_sch", ServerRef, "AdventureWorks", "FactResellerSales");
        collected.Facts.Add(new LineageFact
        {
            ViaModuleKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Warehouse_Report"),
            Relation = LineageRelation.Reads,
            ServerRef = "${env:SQLFLOW_CONN_DWH}",
            Database = "AdventureWorks",
            Schema = "dbo",
            Name = "FactResellerSales",
            Tier = LineageTier.Declared,
        });

        var report = Build(collected);

        Assert.Equal(
            NodeKey.For("${env:SQLFLOW_CONN_DWH}", "AdventureWorks", "dbo", "FactResellerSales"),
            Assert.Single(report.Edges, e => e.Relation == LineageRelation.Reads).ObjectKey);
    }
}
