using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

public sealed class YamlFlowLoaderTests
{
    private readonly YamlFlowLoader _loader = new();

    private const string ValidYaml =
        """
        name: orders
        source:
          type: csv
          location: ./orders.csv
          options:
            delimiter: ","
            header: true
        target:
          connection: ${env:SQLFLOW_DW}
          schema: dbo
          table: Orders
        schema:
          evolve: widen
          overrides:
            OrderId:
              type: BIGINT
              nullable: false
        load:
          mode: truncate-load
          batchSize: 1000
        """;

    [Fact]
    public void Parse_Valid_MapsModel()
    {
        var flow = _loader.Parse(ValidYaml);

        Assert.Equal("orders", flow.Name);
        Assert.Equal("csv", flow.Source.Type);
        Assert.Equal("./orders.csv", flow.Source.Location);
        Assert.Equal(",", flow.Source.Options["delimiter"]);
        Assert.Equal("dbo", flow.Target.Schema);
        Assert.Equal(SchemaEvolution.Widen, flow.Schema.Evolve);
        Assert.True(flow.Schema.Overrides.ContainsKey("OrderId"));
        Assert.Equal("BIGINT", flow.Schema.Overrides["OrderId"].Type);
        Assert.Equal(LoadMode.TruncateLoad, flow.Load.Mode);
        Assert.Equal(1000, flow.Load.BatchSize);
    }

    [Fact]
    public void Parse_NoBatch_LeavesBatchNull()
    {
        var flow = _loader.Parse(ValidYaml);

        Assert.Null(flow.Batch);
    }

    [Fact]
    public void Parse_Batch_Mapped()
    {
        const string yaml =
            """
            name: x
            batch: apc-dalane
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            """;

        var flow = _loader.Parse(yaml);

        Assert.Equal("apc-dalane", flow.Batch);
    }

    [Fact]
    public void Parse_BlankBatch_NormalizedToNull()
    {
        const string yaml =
            """
            name: x
            batch: "   "
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            """;

        var flow = _loader.Parse(yaml);

        Assert.Null(flow.Batch);
    }

    [Fact]
    public void Parse_PreAndPostProcess_Mapped()
    {
        const string yaml =
            """
            name: x
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            preProcess:
              - "EXEC dbo.Pre"
            postProcess:
              - "EXEC dbo.Post"
              - "UPDATE STATISTICS dbo.T"
            """;

        var flow = _loader.Parse(yaml);

        Assert.Equal("EXEC dbo.Pre", Assert.Single(flow.PreProcess));
        Assert.Equal(2, flow.PostProcess.Count);
    }

    [Fact]
    public void Parse_MissingTarget_Throws()
    {
        const string yaml =
            """
            name: x
            source:
              type: csv
              location: ./x.csv
            """;

        Assert.Throws<FlowValidationException>(() => _loader.Parse(yaml));
    }

    [Fact]
    public void Parse_InvalidEvolve_Throws()
    {
        const string yaml =
            """
            name: x
            source: { type: csv, location: ./x.csv }
            target: { connection: c, schema: dbo, table: T }
            schema: { evolve: nonsense }
            """;

        Assert.Throws<FlowValidationException>(() => _loader.Parse(yaml));
    }
}
