using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The authored pre-ingestion transform layer: YAML load + validation, the @ColName placeholder, the
/// declared/inferred/pass-through merge, and the emitted transformation-view DDL. All pure and deterministic.
/// </summary>
public sealed class ColumnTransformTests
{
    private readonly YamlFlowLoader _loader = new();

    private static string Yaml(string transformBlock) =>
        $$"""
        name: moveabout-status
        source:
          type: csv
          location: ./status.csv
        target:
          connection: ${env:DW}
          schema: dbo
          table: Moveabout_Status
        {{transformBlock}}
        """;

    // ---- YAML mapping ----

    [Fact]
    public void Parse_TransformColumns_MapsEveryField()
    {
        var flow = _loader.Parse(Yaml(
            """
            transform:
              inferTypes: true
              generateView: true
              columns:
                - name: vehicle_type
                  expr: "CAST(@ColName AS varchar(50))"
                  as: vehicle_type_clean
                  type: varchar(50)
                  order: 10
                - name: line_total
                  type: decimal(18,2)
                - name: ingested_at
                  expr: "SYSUTCDATETIME()"
                  virtual: true
                  order: 99
                - name: scratch
                  type: int
                  excludeFromView: true
            """));

        Assert.True(flow.Inference.Enabled);
        Assert.True(flow.Inference.GenerateView);
        Assert.Equal(4, flow.Inference.Columns.Count);

        var vehicle = flow.Inference.Columns[0];
        Assert.Equal("vehicle_type", vehicle.Name);
        Assert.Equal("CAST(@ColName AS varchar(50))", vehicle.Expression);
        Assert.Equal("vehicle_type_clean", vehicle.Alias);
        Assert.Equal("varchar(50)", vehicle.Type);
        Assert.Equal(10, vehicle.SortOrder);
        Assert.False(vehicle.Virtual);

        Assert.True(flow.Inference.Columns[2].Virtual);
        Assert.True(flow.Inference.Columns[3].ExcludeFromView);
    }

    [Fact]
    public void Parse_GenerateView_DefaultsOn_EvenWithNoTransformBlock()
    {
        var flow = _loader.Parse(Yaml(string.Empty));
        Assert.True(flow.Inference.GenerateView);
        Assert.Empty(flow.Inference.Columns);
    }

    [Fact]
    public void Parse_GenerateView_CanBeDisabled()
    {
        var flow = _loader.Parse(Yaml(
            """
            transform:
              generateView: false
            """));
        Assert.False(flow.Inference.GenerateView);
    }

    [Theory]
    [InlineData("columns:\n    - expr: \"1\"", "missing 'name'")]
    [InlineData("columns:\n    - name: a\n      type: int\n    - name: A\n      type: int", "duplicate column 'A'")]
    [InlineData("columns:\n    - name: a\n      virtual: true", "must declare an 'expr'")]
    [InlineData("columns:\n    - name: a\n      virtual: true\n      expr: \"CAST(@ColName AS int)\"", "cannot use @ColName")]
    [InlineData("columns:\n    - name: a", "does nothing")]
    public void Parse_InvalidTransformColumn_Throws(string columnsBlock, string expectedMessage)
    {
        var ex = Assert.Throws<FlowValidationException>(() => _loader.Parse(Yaml($"transform:\n  {columnsBlock}")));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IngestionFlow_CarriesTheSameTransformBlock()
    {
        // The relational (ing) document shares the transform block shape and validation with the file flow, so
        // an external-DB landing declares its transforms identically.
        var document = new YamlIngestionFlowLoader().Parse(
            """
            flowType: ing
            name: ext-orders
            source:
              connection: Server=src;Database=d;Integrated Security=True
              object: dbo.Orders
            target:
              connection: Server=trg;Database=pre;Integrated Security=True
              object: dbo.Orders
            transform:
              inferTypes: true
              columns:
                - name: amount
                  type: decimal(18,2)
            """);

        Assert.True(document.Flow.Transform.Enabled);
        Assert.True(document.Flow.Transform.GeneratesView);
        var column = Assert.Single(document.Flow.Transform.Columns);
        Assert.Equal("amount", column.Name);
        Assert.Equal("decimal(18,2)", column.Type);
    }

    [Fact]
    public void GeneratesView_RequiresTheToggleAndSomethingToProject()
    {
        Assert.False(new TypeInferencePolicy().GeneratesView);                                  // nothing to project
        Assert.True(new TypeInferencePolicy { Enabled = true }.GeneratesView);                  // inference
        Assert.True(new TypeInferencePolicy { Columns = [new ColumnTransform { Name = "a", Type = "int" }] }.GeneratesView);
        Assert.False(new TypeInferencePolicy { Enabled = true, GenerateView = false }.GeneratesView); // opted out
    }

    // ---- @ColName substitution ----

    [Theory]
    [InlineData("CAST(@ColName AS varchar(50))", "CAST([vehicle_type] AS varchar(50))")]
    [InlineData("UPPER(@colname)", "UPPER([vehicle_type])")]
    [InlineData("COALESCE(@ColName, @ColName)", "COALESCE([vehicle_type], [vehicle_type])")]
    [InlineData("LEN(@ColNameSuffix)", "LEN(@ColNameSuffix)")]
    public void Substitute_ReplacesWholeTokenOnly(string expression, string expected)
    {
        Assert.Equal(expected, ColumnTransformExpression.Substitute(expression, "[vehicle_type]"));
    }

    [Fact]
    public void Substitute_TreatsColumnReferenceLiterally_NoRegexInjection()
    {
        // A column named with a '$' must not be interpreted as a regex replacement group.
        Assert.Equal("CAST([a$1b] AS int)", ColumnTransformExpression.Substitute("CAST(@ColName AS int)", "[a$1b]"));
    }

    [Theory]
    [InlineData("@ColName", true)]
    [InlineData("@ColNameX", false)]
    [InlineData("X@ColName", false)]
    [InlineData("SYSUTCDATETIME()", false)]
    public void ReferencesColumnToken_IsTokenAware(string expression, bool expected)
    {
        Assert.Equal(expected, ColumnTransformExpression.ReferencesColumnToken(expression));
    }

    // ---- Resolver merge ----

    private static TypeInferencePolicy Policy(params ColumnTransform[] columns) => new() { Columns = columns };

    [Fact]
    public void Resolve_DeclaredOverridesInference_ForSameColumn()
    {
        var inferred = new[]
        {
            new InferredColumn { ColumnName = "amount", DataType = "int", SelectExpression = "TRY_CONVERT(int, [amount])", Converted = true },
        };
        var resolved = ColumnTransformResolver.Resolve(
            ["amount"],
            Policy(new ColumnTransform { Name = "amount", Type = "decimal(18,2)" }),
            inferred);

        var column = Assert.Single(resolved);
        Assert.Equal("amount", column.ColumnName);
        Assert.Equal("CAST([amount] AS decimal(18,2))", column.SelectExpression);
    }

    [Fact]
    public void Resolve_InferenceFillsColumns_NoAuthorNamed()
    {
        var inferred = new[]
        {
            new InferredColumn { ColumnName = "amount", DataType = "int", SelectExpression = "TRY_CONVERT(int, [amount])", Converted = true },
        };
        var resolved = ColumnTransformResolver.Resolve(["id", "amount"], Policy(), inferred);

        Assert.Equal(["id", "amount"], resolved.Select(c => c.ColumnName));
        Assert.Equal("[id]", resolved[0].SelectExpression);               // pass-through
        Assert.Equal("TRY_CONVERT(int, [amount])", resolved[1].SelectExpression); // inferred
    }

    [Fact]
    public void Resolve_ExcludeFromView_DropsColumnFromProjection()
    {
        var resolved = ColumnTransformResolver.Resolve(
            ["keep", "scratch"],
            Policy(
                new ColumnTransform { Name = "scratch", Type = "int", ExcludeFromView = true }));

        var column = Assert.Single(resolved);
        Assert.Equal("keep", column.ColumnName);
    }

    [Fact]
    public void Resolve_VirtualColumn_AppendedAfterRawColumns()
    {
        var resolved = ColumnTransformResolver.Resolve(
            ["a", "b"],
            Policy(new ColumnTransform { Name = "loaded_at", Expression = "SYSUTCDATETIME()", Virtual = true }));

        Assert.Equal(["a", "b", "loaded_at"], resolved.Select(c => c.ColumnName));
        Assert.Equal("SYSUTCDATETIME()", resolved[2].SelectExpression);
    }

    [Fact]
    public void Resolve_Alias_RenamesOutputButReferencesRawColumn()
    {
        var resolved = ColumnTransformResolver.Resolve(
            ["vehicle_type"],
            Policy(new ColumnTransform { Name = "vehicle_type", Type = "varchar(50)", Alias = "vehicle_type_clean" }));

        var column = Assert.Single(resolved);
        Assert.Equal("vehicle_type_clean", column.ColumnName);
        Assert.Equal("CAST([vehicle_type] AS varchar(50))", column.SelectExpression);
    }

    [Fact]
    public void Resolve_SortOrder_OrdersColumns()
    {
        var resolved = ColumnTransformResolver.Resolve(
            ["a", "b", "c"],
            Policy(
                new ColumnTransform { Name = "a", Type = "int", SortOrder = 3 },
                new ColumnTransform { Name = "b", Type = "int", SortOrder = 1 },
                new ColumnTransform { Name = "c", Type = "int", SortOrder = 2 }));

        Assert.Equal(["b", "c", "a"], resolved.Select(x => x.ColumnName));
    }

    [Fact]
    public void Resolve_SortOrder_IsAbsolutePositionOnTheNaturalAxis()
    {
        // SortOrder shares one axis with natural position: a high order pulls a column toward the end while
        // unordered columns keep their natural slots. Here b (order 20) lands last.
        var resolved = ColumnTransformResolver.Resolve(
            ["a", "b", "c"],
            Policy(new ColumnTransform { Name = "b", Type = "int", SortOrder = 20 }));

        Assert.Equal(["a", "c", "b"], resolved.Select(x => x.ColumnName));
    }

    [Fact]
    public void Resolve_NoPolicyNoInference_PassesEveryColumnThrough()
    {
        var resolved = ColumnTransformResolver.Resolve(["a", "b"], Policy());
        Assert.Equal(["a", "b"], resolved.Select(c => c.ColumnName));
        Assert.All(resolved, c => Assert.False(c.Converted));
    }

    // ---- View DDL ----

    [Fact]
    public void BuildView_EmitsCreateOrAlterOverRawTable()
    {
        var resolved = ColumnTransformResolver.Resolve(
            ["vehicle_type"],
            Policy(new ColumnTransform { Name = "vehicle_type", Type = "varchar(50)", Alias = "vehicle_type_clean" }));

        var ddl = TransformViewBuilder.Build("pre", "vMoveabout_Status", "pre", "Moveabout_Status_raw", resolved);

        Assert.StartsWith("CREATE OR ALTER VIEW [pre].[vMoveabout_Status]", ddl, StringComparison.Ordinal);
        Assert.Contains("CAST([vehicle_type] AS varchar(50)) AS [vehicle_type_clean]", ddl, StringComparison.Ordinal);
        Assert.Contains("FROM [pre].[Moveabout_Status_raw]", ddl, StringComparison.Ordinal);
        Assert.EndsWith(";", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildView_EmptyProjection_SelectsStar()
    {
        var ddl = TransformViewBuilder.Build("pre", "vT", "pre", "T_raw", []);
        Assert.Contains("SELECT *", ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildView_WhereClause_AppendedAsPreFilter()
    {
        var resolved = ColumnTransformResolver.Resolve(["a"], Policy());
        var ddl = TransformViewBuilder.Build("pre", "vT", "pre", "T_raw", resolved, "a IS NOT NULL");
        Assert.Contains("WHERE a IS NOT NULL", ddl, StringComparison.Ordinal);
    }
}
