using Microsoft.Data.SqlClient;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.PowerBi;
using SqlFlow.Lineage.PowerQuery;
using Xunit;

namespace SqlFlow.Tests.PowerBi;

/// <summary>
/// Translating a report visual's query into T-SQL over the source tables. The committed AdventureWorks Sales
/// specification is the realistic case (every visual translates, including a measure over an inactive relationship);
/// small synthetic models pin each Power Query step, each DAX form, the filters, and every refusal.
/// </summary>
public sealed class VisualSqlTranslatorTests
{
    // ---- The sample report ----------------------------------------------------------------------------------

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlFlow.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }

    private static (LineageSubscriberModel Model, IReadOnlyList<PbixVisual> Visuals) Sample()
    {
        var path = RepoFile("samples", "powerai-adventureworks", "reports", "AdventureWorks Sales.pbix.yaml");
        var result = PbixExtractTool.Parse(File.ReadAllText(path), "sample");
        var model = new LineageSubscriberModel
        {
            ReportFile = "AdventureWorks Sales.pbix",
            Tables = result.Model.Tables.Select(t => new LineageSubscriberModelTable
            {
                Name = t.Name,
                PowerQuery = t.PowerQuery,
                Fields = t.Fields.Select(f => new LineageSubscriberModelField
                {
                    Name = f.Name, Kind = f.Kind, DataType = f.DataType, Expression = f.Expression,
                }).ToList(),
            }).ToList(),
            Relationships = result.Model.Relationships.Select(r => new LineageSubscriberModelRelationship
            {
                FromTable = r.FromTable, FromColumn = r.FromColumn, ToTable = r.ToTable, ToColumn = r.ToColumn,
                Cardinality = r.Cardinality, IsActive = r.IsActive,
            }).ToList(),
            Expressions = result.Model.Expressions
                .Select(e => new LineageSubscriberExpression { Name = e.Name, PowerQuery = e.PowerQuery })
                .ToList(),
        };
        return (model, result.Pages.SelectMany(p => p.Visuals ?? []).ToList());
    }

    [Fact]
    public void TheSampleSpecification_CarriesTheSharedQueriesItsTablesMerge()
    {
        var (model, _) = Sample();

        Assert.Equal(["DimGeography", "DimProductCategory", "DimProductSubcategory"], model.Expressions.Select(e => e.Name));
    }

    [Fact]
    public void EverySampleVisual_Translates()
    {
        var (model, visuals) = Sample();
        var translator = new VisualSqlTranslator(model, _ => null);

        Assert.Equal(5, visuals.Count);
        Assert.All(visuals, visual =>
        {
            var translation = translator.Translate(visual.Sql!);
            Assert.True(translation.Problem is null, $"{visual.Title}: {translation.Problem}");
        });
    }

    [Fact]
    public void AVisualOverAMergedQuery_JoinsItsSourceTables_AndGroupsByTheRenamedColumn()
    {
        var (model, visuals) = Sample();

        var translation = new VisualSqlTranslator(model, _ => null)
            .Translate(visuals.Single(v => v.Title == "Order Quantity by Reseller Country").Sql!);

        Assert.Equal(
            "SELECT [t0].[EnglishCountryRegionName] AS [Reseller.Country-Region], SUM([t1].[OrderQuantity]) AS [Sum(Sales.Order Quantity)]\n"
            + "FROM [AdventureWorks].[dbo].[FactResellerSales] AS [t1]\n"
            + "LEFT JOIN ([AdventureWorks].[dbo].[DimReseller] AS [t2] LEFT JOIN [AdventureWorks].[dbo].[DimGeography] AS [t0] "
            + "ON [t2].[GeographyKey] = [t0].[GeographyKey]) ON [t1].[ResellerKey] = [t2].[ResellerKey]\n"
            + "GROUP BY [t0].[EnglishCountryRegionName]",
            translation.Sql);
    }

    [Fact]
    public void AMeasureOverAnInactiveRelationship_IsComputedOverItsOwnJoin_AndJoinedBackOnTheKeys()
    {
        var (model, visuals) = Sample();

        var sql = new VisualSqlTranslator(model, _ => null).Translate(visuals.Single(v => v.Title is null).Sql!).Sql;

        Assert.NotNull(sql);
        Assert.StartsWith("WITH [q0] AS (", sql, StringComparison.Ordinal);
        Assert.Contains("ON [t1].[OrderDateKey] = [t0].[DateKey]", sql, StringComparison.Ordinal);
        Assert.Contains("ON [t1].[DueDateKey] = [t0].[DateKey]", sql, StringComparison.Ordinal);
        Assert.Contains(
            "SELECT COALESCE([q0].[k0], [q1].[k0]) AS [Date.Fiscal.Month], [q0].[a0] AS [Sum(Sales.Sales Amount)], "
            + "[q1].[a0] AS [Sales.Sales Amount by Due Date]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("FULL OUTER JOIN [q1] ON ([q0].[k0] = [q1].[k0] OR ([q0].[k0] IS NULL AND [q1].[k0] IS NULL))", sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY [Date.Fiscal.Month] ASC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceColumnTheRegisteredTableLacks_IsRefused()
    {
        var (model, visuals) = Sample();
        var translator = new VisualSqlTranslator(model, table => table.Name == "DimGeography" ? ["GeographyKey", "City"] : null);

        var translation = translator.Translate(visuals.Single(v => v.Title == "Order Quantity by Reseller Country").Sql!);

        Assert.Null(translation.Sql);
        Assert.Contains("AdventureWorks.dbo.DimGeography has no column 'EnglishCountryRegionName'", translation.Problem, StringComparison.Ordinal);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task EverySampleTranslation_RunsOnAdventureWorks()
    {
        var connectionString = Environment.GetEnvironmentVariable("SQLFLOW_ADVENTUREWORKS_DB");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set SQLFLOW_ADVENTUREWORKS_DB to an AdventureWorksDW2022 database.");

        var (model, visuals) = Sample();
        var translator = new VisualSqlTranslator(model, _ => null);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var visual in visuals)
        {
            await using var command = new SqlCommand(translator.Translate(visual.Sql!).Sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"'{visual.Title}' returned no rows");
        }
    }

    // ---- Power Query ----------------------------------------------------------------------------------------

    private const string Navigation = """
        Source = Sql.Database("server", "Db"),
        dbo_T = Source{[Schema="dbo",Item="T"]}[Data]
        """;

    private static Relation Resolve(string steps, string result, IReadOnlyDictionary<string, string>? queries = null)
        => PowerQueryLineage.Resolve($"let\n{Navigation},\n{steps}\nin\n{result}", queries ?? new Dictionary<string, string>());

    private static string Render(Relation relation, string column)
    {
        var value = relation.Column(column);
        Assert.True(value.Expression is not null, value.Problem);
        return new SqlRenderer().Scalar(value.Expression);
    }

    [Fact]
    public void Steps_SelectRenameDuplicateAndRemove_MapToTheSourceColumns()
    {
        var relation = Resolve(
            """
            #"Kept" = Table.SelectColumns(dbo_T, {"A", "B", "C"}),
            #"Renamed" = Table.RenameColumns(#"Kept", {{"A", "Alpha"}, {"B", "Beta"}}),
            #"Copied" = Table.DuplicateColumn(#"Renamed", "Alpha", "Alpha Copy"),
            #"Dropped" = Table.RemoveColumns(#"Copied", {"C"}),
            #"Typed" = Table.TransformColumnTypes(#"Dropped", {{"Beta", type text}, {"Alpha", Int64.Type}})
            """,
            "#\"Typed\"");

        Assert.Null(relation.Problem);
        Assert.Equal("[t0].[A]", Render(relation, "Alpha"));
        Assert.Equal("[t0].[B]", Render(relation, "Beta"));
        Assert.Equal("[t0].[A]", Render(relation, "Alpha Copy"));
        Assert.NotNull(relation.Column("C").Problem);
        Assert.NotNull(relation.Column("A").Problem);
    }

    [Fact]
    public void AnAddedColumn_BecomesItsTsqlExpression()
    {
        var relation = Resolve(
            """
            #"Named" = Table.AddColumn(dbo_T, "Full Name", each [First] & " " & [Last], type text),
            #"Keyed" = Table.AddColumn(#"Named", "MonthKey", each [Year] * 100 + [Month]),
            #"Labelled" = Table.AddColumn(#"Keyed", "Label", each [Code] & "-" & Text.From([Line])),
            #"Constant" = Table.AddColumn(#"Labelled", "Channel", each "Reseller")
            """,
            "#\"Constant\"");

        Assert.Equal(
            "(CAST([t0].[First] AS nvarchar(4000)) + N' ' + CAST([t0].[Last] AS nvarchar(4000)))",
            Render(relation, "Full Name"));
        Assert.Equal("(([t0].[Year] * 100) + [t0].[Month])", Render(relation, "MonthKey"));
        Assert.Equal(
            "(CAST([t0].[Code] AS nvarchar(4000)) + N'-' + CAST(CAST([t0].[Line] AS nvarchar(4000)) AS nvarchar(4000)))",
            Render(relation, "Label"));
        Assert.Equal("N'Reseller'", Render(relation, "Channel"));
    }

    [Fact]
    public void AnAddedColumnOutsideTheSubset_IsRefusedAlone()
    {
        var relation = Resolve(
            """#"Added" = Table.AddColumn(dbo_T, "Upper", each Text.Upper([Name]))""",
            "#\"Added\"");

        Assert.Null(relation.Problem);
        Assert.Contains("Text.Upper", relation.Column("Upper").Problem, StringComparison.Ordinal);
        Assert.Equal("[t0].[Name]", Render(relation, "Name"));
    }

    [Fact]
    public void AMergedQuery_IsJoinedAndItsExpandedColumnsMapped()
    {
        var queries = new Dictionary<string, string>
        {
            ["Geo"] = """
                let
                    Source = Sql.Database("server", "Db"),
                    dbo_G = Source{[Schema="dbo",Item="G"]}[Data],
                    #"Renamed" = Table.RenameColumns(dbo_G, {{"CountryName", "Country"}})
                in
                    #"Renamed"
                """,
        };

        var relation = Resolve(
            """
            #"Merged" = Table.NestedJoin(dbo_T, {"GeoKey"}, Geo, {"GeoKey"}, "Geo", JoinKind.LeftOuter),
            #"Expanded" = Table.ExpandTableColumn(#"Merged", "Geo", {"Country"}, {"Country Name"})
            """,
            "#\"Expanded\"",
            queries);

        Assert.Null(relation.Problem);
        var renderer = new SqlRenderer();
        Assert.Equal(
            "([Db].[dbo].[T] AS [t0] LEFT JOIN [Db].[dbo].[G] AS [t1] ON [t0].[GeoKey] = [t1].[GeoKey])",
            renderer.Relation(relation));
        Assert.Equal("[t1].[CountryName]", renderer.Scalar(relation.Column("Country Name").Expression!));
    }

    [Fact]
    public void AMergeNeverExpanded_IsDroppedWhenLeft_AndRefusedWhenInner()
    {
        var queries = new Dictionary<string, string>
        {
            ["Other"] = "let Source = Sql.Database(\"s\", \"Db\"), o = Source{[Schema=\"dbo\",Item=\"O\"]}[Data] in o",
        };

        var left = Resolve(
            """#"Merged" = Table.NestedJoin(dbo_T, {"K"}, Other, {"K"}, "Other", JoinKind.LeftOuter)""",
            "#\"Merged\"",
            queries);
        Assert.Null(left.Problem);
        Assert.Empty(left.Joins);

        var inner = Resolve(
            """#"Merged" = Table.NestedJoin(dbo_T, {"K"}, Other, {"K"}, "Other", JoinKind.Inner)""",
            "#\"Merged\"",
            queries);
        Assert.Contains("inner merge", inner.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""#"Filtered" = Table.SelectRows(dbo_T, each [A] > 1)""", "#\"Filtered\"", "changes which rows")]
    [InlineData("""#"Grouped" = Table.Group(dbo_T, {"A"}, {})""", "#\"Grouped\"", "changes which rows")]
    [InlineData("""#"Custom" = Table.Buffer(dbo_T)""", "#\"Custom\"", "Table.Buffer")]
    [InlineData("""#"Merged" = Table.NestedJoin(dbo_T, {"K"}, Missing, {"K"}, "M", JoinKind.LeftOuter)""", "#\"Merged\"", "'Missing'")]
    [InlineData("""#"Merged" = Table.NestedJoin(dbo_T, {"K"}, dbo_T, {"K"}, "M", JoinKind.FullOuter)""", "#\"Merged\"", "JoinKind.FullOuter")]
    public void ARowChangingOrUnknownStep_RefusesTheWholeQuery(string steps, string result, string expected)
    {
        var relation = Resolve(steps, result);

        Assert.Contains(expected, relation.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceThatIsNotADatabaseTable_IsRefused()
    {
        var inline = PowerQueryLineage.Resolve(
            "let Source = Table.FromRows(Json.Document(\"[]\"), type table [A = _t]) in Source",
            new Dictionary<string, string>());
        var native = PowerQueryLineage.Resolve(
            "let Source = Sql.Database(\"s\", \"Db\", [Query=\"select 1\"]) in Source",
            new Dictionary<string, string>());

        Assert.NotNull(inline.Problem);
        Assert.Contains("native SQL query", native.Problem, StringComparison.Ordinal);
    }

    // ---- DAX and the translator on a small model ------------------------------------------------------------

    private static LineageSubscriberModel Model(params (string Name, string Dax)[] measures)
    {
        static LineageSubscriberModelTable Table(string name, string source, params LineageSubscriberModelField[] fields) => new()
        {
            Name = name,
            PowerQuery = $"let Source = Sql.Database(\"s\", \"Db\"), t = Source{{[Schema=\"dbo\",Item=\"{source}\"]}}[Data] in t",
            Fields = fields,
        };

        static LineageSubscriberModelField Column(string name) => new() { Name = name, Kind = "column" };

        return new LineageSubscriberModel
        {
            ReportFile = "r.pbix",
            Tables =
            [
                Table("Sales", "FactSales",
                    [Column("Amount"), Column("Qty"), Column("OrderKey"), Column("ShipKey"), Column("ProductKey"),
                     .. measures.Select(m => new LineageSubscriberModelField { Name = m.Name, Kind = "measure", Expression = m.Dax })]),
                Table("Date", "DimDate", Column("DateKey"), Column("Year")),
                Table("Product", "DimProduct", Column("ProductKey"), Column("Color")),
                Table("Budget", "FactBudget", Column("Amount")),
            ],
            Relationships =
            [
                new() { FromTable = "Sales", FromColumn = "OrderKey", ToTable = "Date", ToColumn = "DateKey", Cardinality = "M:1", IsActive = true },
                new() { FromTable = "Sales", FromColumn = "ShipKey", ToTable = "Date", ToColumn = "DateKey", Cardinality = "M:1", IsActive = false },
                new() { FromTable = "Sales", FromColumn = "ProductKey", ToTable = "Product", ToColumn = "ProductKey", Cardinality = "M:1", IsActive = true },
            ],
        };
    }

    private static VisualTranslation Translate(string sql, params (string Name, string Dax)[] measures)
        => new VisualSqlTranslator(Model(measures), _ => null).Translate(sql);

    [Theory]
    [InlineData("SUM(Sales[Amount])", "SUM([t1].[Amount])")]
    [InlineData("AVERAGE(Sales[Amount])", "AVG(CAST([t1].[Amount] AS float))")]
    [InlineData("MIN('Sales'[Amount])", "MIN([t1].[Amount])")]
    [InlineData("MAX([Amount])", "MAX([t1].[Amount])")]
    [InlineData("COUNT(Sales[Qty])", "COUNT([t1].[Qty])")]
    [InlineData("DISTINCTCOUNT(Sales[ProductKey])", "COUNT(DISTINCT [t1].[ProductKey])")]
    [InlineData("COUNTROWS(Sales)", "COUNT_BIG(*)")]
    [InlineData("DIVIDE(SUM(Sales[Amount]), SUM(Sales[Qty]))", "(CAST(SUM([t1].[Amount]) AS float) / NULLIF(SUM([t1].[Qty]), 0))")]
    [InlineData("DIVIDE(SUM(Sales[Amount]), SUM(Sales[Qty]), 0)", "COALESCE(CAST(SUM([t1].[Amount]) AS float) / NULLIF(SUM([t1].[Qty]), 0), 0)")]
    [InlineData("SUM(Sales[Amount]) - SUM(Sales[Qty]) * 2", "(SUM([t1].[Amount]) - (SUM([t1].[Qty]) * 2))")]
    [InlineData("[Base] * 1.5", "(SUM([t1].[Amount]) * 1.5)")]
    public void AMeasure_BecomesItsTsqlAggregate(string dax, string expected)
    {
        var translation = Translate(
            "SELECT [d].[Year] AS [Year], [s].[M] AS [M] FROM [Date] AS [d], [Sales] AS [s];",
            ("M", dax), ("Base", "SUM(Sales[Amount])"));

        Assert.True(translation.Problem is null, translation.Problem);
        Assert.Contains($"{expected} AS [M]", translation.Sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY [t0].[Year]", translation.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SAMEPERIODLASTYEAR(Sales[Amount])", "SAMEPERIODLASTYEAR")]
    [InlineData("CALCULATE(SUM(Sales[Amount]), Product[Color] = \"Red\")", "only USERELATIONSHIP")]
    [InlineData("SUMX(Sales, Sales[Amount] * Sales[Qty])", "SUMX")]
    [InlineData("Sales[Amount]", "outside an aggregation")]
    [InlineData("SUM(Sales[Amount]) + CALCULATE(SUM(Sales[Amount]), USERELATIONSHIP(Sales[ShipKey], 'Date'[DateKey]))", "different relationships")]
    public void AMeasureOutsideTheSubset_IsRefusedByName(string dax, string expected)
    {
        var translation = Translate("SELECT [s].[M] AS [M] FROM [Sales] AS [s];", ("M", dax));

        Assert.Null(translation.Sql);
        Assert.Contains(expected, translation.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasureAloneOverAnInactiveRelationship_UsesThatRelationship()
    {
        var translation = Translate(
            "SELECT [d].[Year] AS [Year], [s].[Shipped] AS [Shipped] FROM [Date] AS [d], [Sales] AS [s];",
            ("Shipped", "CALCULATE(SUM(Sales[Amount]), USERELATIONSHIP('Date'[DateKey], Sales[ShipKey]))"));

        Assert.True(translation.Problem is null, translation.Problem);
        Assert.Contains("ON [t1].[ShipKey] = [t0].[DateKey]", translation.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OrderKey", translation.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Filters_AreWrittenOverTheSourceColumns_WithBlanksAsNull()
    {
        var translation = Translate(
            "SELECT [p].[Color] AS [Color], SUM([s].[Amount]) AS [Total] FROM [Product] AS [p], [Sales] AS [s], [Date] AS [d] "
            + "WHERE [p].[Color] IN ('Red', NULL) AND ([d].[Year] >= 2020) AND NOT ([p].[Color] = NULL);");

        Assert.True(translation.Problem is null, translation.Problem);
        Assert.Contains(
            "LEFT JOIN [Db].[dbo].[DimDate] AS [t2] ON [t1].[OrderKey] = [t2].[DateKey]",
            translation.Sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE ((([t0].[Color] IN (N'Red') OR [t0].[Color] IS NULL) AND (([t2].[Year] >= 2020))) "
            + "AND NOT (([t0].[Color] IS NULL)))",
            translation.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TablesNoRelationshipConnects_AreRefused()
    {
        var translation = Translate("SELECT [p].[Color] AS [Color], SUM([b].[Amount]) AS [Budget] FROM [Product] AS [p], [Budget] AS [b];");

        Assert.Null(translation.Sql);
        Assert.Contains("no table of the model relates", translation.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilterOnAMeasure_IsRefused()
    {
        var translation = Translate(
            "SELECT [p].[Color] AS [Color] FROM [Product] AS [p], [Sales] AS [s] WHERE [s].[M] > 1;",
            ("M", "SUM(Sales[Amount])"));

        Assert.Contains("filters on the measure", translation.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGraph_TranslatesAVisualsQuery_AndLeavesADeclaredQueryAlone()
    {
        var model = Model();
        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Report");
        var collected = new CollectionResult();
        collected.Subscribers.Add(new CollectedSubscriber
        {
            Subscriber = new global::SqlFlow.Core.Subscribers.DataSubscriber { Name = "Report", Type = "PowerBI" },
            NodeKey = subscriberKey,
            File = "subscribers.yaml",
            Queries =
            [
                new CollectedSubscriberQuery
                {
                    Name = "Page 1 / Colors",
                    ServerRef = "${env:DW}",
                    Sql = "SELECT [p].[Color] AS [Color] FROM [Product] AS [p];",
                    ReportFile = "r.pbix",
                    Objects = [],
                },
                new CollectedSubscriberQuery
                {
                    Name = "Declared",
                    ServerRef = "${env:DW}",
                    Sql = "SELECT Color FROM dbo.DimProduct",
                    Objects = [],
                },
                new CollectedSubscriberQuery
                {
                    Name = "Page 2 / Missing",
                    ServerRef = "${env:DW}",
                    Sql = "SELECT [x].[A] AS [A] FROM [Nowhere] AS [x];",
                    ReportFile = "r.pbix",
                    Objects = [],
                },
            ],
            Models = [model],
        });

        var report = global::SqlFlow.Lineage.Graph.LineageGraphBuilder.Build(
            collected, "flows", [LineageTier.Declared], new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc));

        var queries = Assert.Single(report.Subscribers).Queries;
        Assert.StartsWith("SELECT [t0].[Color] AS [Color]", queries[0].SourceSql, StringComparison.Ordinal);
        Assert.Null(queries[0].TranslationProblem);
        Assert.Null(queries[1].SourceSql);
        Assert.Null(queries[1].TranslationProblem);
        Assert.Null(queries[2].SourceSql);
        Assert.Contains("'Nowhere', which is not a table of the model", queries[2].TranslationProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void AVisualWithoutAggregates_GroupsByItsColumns()
    {
        var translation = Translate("SELECT [p].[Color] AS [Color] FROM [Product] AS [p];");

        Assert.Equal(
            "SELECT [t0].[Color] AS [Color]\nFROM [Db].[dbo].[DimProduct] AS [t0]\nGROUP BY [t0].[Color]",
            translation.Sql);
    }
}
