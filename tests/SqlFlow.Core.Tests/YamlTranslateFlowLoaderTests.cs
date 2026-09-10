using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Translate;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The translate (flowType: trl) loader: envelope validation, the template dialect's compilation rules, and the
/// output/invoke cross-field constraints. The happy-path template mirrors the OSDU dataset--File.Generic record
/// (Examples/dataset in the OSDU data-definitions repository): a record envelope with constant kind, ACL arrays,
/// and a nested data block, which is exactly the class of document the dialect exists to produce.
/// </summary>
public sealed class YamlTranslateFlowLoaderTests
{
    private static readonly YamlTranslateFlowLoader Loader = new();

    private const string OsduDatasetFlow = """
        flowType: trl
        name: osdu_dataset_01_trl
        batch: dataset
        connections:
          dwh: ${env:DWH}
        source:
          server: dwh
          query: SELECT DatasetId, FileName, FileSource, FileSizeBytes, EncodingFormat, CreatedUtc FROM edw.DatasetFile
        datasets:
          - name: owners
            query: SELECT DatasetId, Principal FROM edw.DatasetAcl WHERE Role = 'owner'
            bind: [DatasetId]
        documents:
          per: row
          nulls: omit
        template:
          id: "kolumbus:dataset--File.Generic:{DatasetId}"
          kind: osdu:wks:dataset--File.Generic:1.1.0
          acl:
            owners:
              $forEach: owners
              $item: "{Principal}"
            viewers: [ "data.default.viewers@kolumbus.osdu.com" ]
          legal:
            legaltags: [ "kolumbus-public" ]
            otherRelevantDataCountries: [ "NO" ]
            status: compliant
          data:
            Name: "{FileName}"
            TotalSize: { $column: FileSizeBytes, $type: string }
            EncodingFormatTypeID: { $column: EncodingFormat, $whenNull: omit }
            DatasetProperties:
              FileSourceInfo:
                FileSource: "{FileSource}"
                Name: "{FileName}"
                PreloadFileCreateDate: { $column: CreatedUtc, $type: dateTime }
        output:
          path: ./out/osdu/dataset
          mode: filePerDocument
          fileName: "dataset_{DatasetId}"
        invoke:
          url: https://osdu.example.com/api/storage/v2/records
          method: PUT
          envelopeKey: records
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}
          reliability:
            skipStatusCodes: [409]
        """;

    [Fact]
    public void Parse_OsduDatasetFlow_CompilesTheFullShape()
    {
        var doc = Loader.Parse(OsduDatasetFlow);
        var flow = doc.Flow;

        Assert.Equal("osdu_dataset_01_trl", flow.SysAlias);
        Assert.Equal("dataset", flow.Batch);
        Assert.Equal("trl", flow.FlowType);
        Assert.Equal(TranslateDocumentGrain.Row, flow.DocumentsPer);
        Assert.Equal(TranslateDocumentNulls.Omit, flow.Nulls);
        Assert.Single(flow.Datasets);
        Assert.Equal(["DatasetId"], flow.Datasets[0].Bind);

        var root = Assert.IsType<TranslateObjectNode>(flow.Template);
        Assert.Equal(["id", "kind", "acl", "legal", "data"], root.Properties.Select(p => p.Name));

        // "id" carries a token: a templated string. "kind" does not: a constant.
        var id = Assert.IsType<TranslateValueNode>(root.Properties[0].Value);
        Assert.Equal(TranslateValueSource.Template, id.Source);
        var kind = Assert.IsType<TranslateValueNode>(root.Properties[1].Value);
        Assert.Equal(TranslateValueSource.Constant, kind.Source);
        Assert.Equal("\"osdu:wks:dataset--File.Generic:1.1.0\"", kind.ConstantJson);

        // acl.owners is a $forEach over the owners dataset whose item is a single-token passthrough.
        var acl = Assert.IsType<TranslateObjectNode>(root.Properties[2].Value);
        var owners = Assert.IsType<TranslateArrayNode>(acl.Properties[0].Value);
        Assert.Equal("owners", owners.ForEach);
        var ownerItem = Assert.IsType<TranslateValueNode>(owners.Item);
        Assert.Equal(TranslateValueSource.Column, ownerItem.Source);
        Assert.Equal("Principal", ownerItem.Column);

        // acl.viewers is a literal sequence of one constant.
        var viewers = Assert.IsType<TranslateListNode>(acl.Properties[1].Value);
        Assert.Single(viewers.Items);

        // data.TotalSize coerces to string (OSDU carries sizes as strings); the nested FileSourceInfo compiles
        // as plain objects because 'Name'/'FileSource' are ordinary property names, not directives.
        var data = Assert.IsType<TranslateObjectNode>(root.Properties[4].Value);
        var totalSize = Assert.IsType<TranslateValueNode>(data.Properties[1].Value);
        Assert.Equal(TranslateValueType.String, totalSize.Type);
        var encoding = Assert.IsType<TranslateValueNode>(data.Properties[2].Value);
        Assert.Equal(TranslateNullPolicy.Omit, encoding.WhenNull);
        var properties = Assert.IsType<TranslateObjectNode>(data.Properties[3].Value);
        var fileSourceInfo = Assert.IsType<TranslateObjectNode>(properties.Properties[0].Value);
        Assert.Equal(["FileSource", "Name", "PreloadFileCreateDate"], fileSourceInfo.Properties.Select(p => p.Name));

        // Output and invoke: per-document files, PUT with an OSDU records envelope, bearer auth via the shared
        // acquire auth surface, 409 tolerated per request, in-order delivery by default.
        Assert.Equal(TranslateOutputMode.FilePerDocument, flow.Output.Mode);
        Assert.Equal("dataset_{DatasetId}", flow.Output.FileName);
        Assert.False(flow.Output.AddTimestamp);
        Assert.NotNull(flow.Invoke);
        var invoke = flow.Invoke!;
        Assert.Equal("PUT", invoke.Method);
        Assert.Equal("records", invoke.EnvelopeKey);
        Assert.Equal(AcquireAuthType.Bearer, invoke.Auth.Type);
        Assert.Equal("${env:OSDU_TOKEN}", invoke.Auth.SecretRef);
        Assert.Equal([409], invoke.Reliability.SkipStatusCodes);
        Assert.Equal(1, invoke.Reliability.Concurrency);
    }

    [Fact]
    public void Parse_ThroughTheDocumentLoader_DispatchesOnTrl()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
            new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
            new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<TranslateFlowDocument>(documents.Parse(OsduDatasetFlow));
        Assert.Equal("osdu_dataset_01_trl", doc.Document.Flow.SysAlias);
    }

    private static string Minimal(string tail) => $$"""
        flowType: trl
        name: t
        source:
          connection: ${env:DWH}
          query: SELECT 1 AS A
        template:
          value: "{A}"
        {{tail}}
        """;

    [Fact]
    public void Parse_WithoutOutput_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal(string.Empty)));
        Assert.Contains("'output' is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MinimalWithOutput_DefaultsToJsonLines()
    {
        var flow = Loader.Parse(Minimal("""
            output:
              path: ./out
            """)).Flow;
        Assert.Equal(TranslateOutputMode.JsonLines, flow.Output.Mode);
        Assert.Null(flow.Invoke);
    }

    [Fact]
    public void Parse_MixedDirectiveAndPlainKeys_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal("""
            output: { path: ./out }
            """).Replace("value: \"{A}\"", "$column: A\n  value: x", StringComparison.Ordinal)));
        Assert.Contains("mixes '$' directives with plain property names", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownDirective_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              value: { $columm: A }
            output: { path: ./out }
            """));
        Assert.Contains("unknown directive '$columm'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForEachOverUndeclaredDataset_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              items:
                $forEach: aliases
                $item: "{A}"
            output: { path: ./out }
            """));
        Assert.Contains("references dataset 'aliases'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForEachRowsAtRowGrain_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              items:
                $forEach: rows
                $item: "{A}"
            output: { path: ./out }
            """));
        Assert.Contains("documents.per: resultSet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ForEachRowsAtResultSetGrain_Compiles()
    {
        var flow = Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            documents: { per: resultSet }
            template:
              items:
                $forEach: rows
                $item: "{A}"
            output: { path: ./out, mode: array }
            """).Flow;
        var root = Assert.IsType<TranslateObjectNode>(flow.Template);
        var array = Assert.IsType<TranslateArrayNode>(root.Properties[0].Value);
        Assert.Equal("rows", array.ForEach);
    }

    [Fact]
    public void Parse_DatasetNamedRows_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: rows
                query: SELECT 1 AS A
                bind: [A]
            template: { value: "{A}" }
            output: { path: ./out }
            """));
        Assert.Contains("reserved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ValueWithType_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              value: { $value: 42, $type: string }
            output: { path: ./out }
            """));
        Assert.Contains("emitted verbatim", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DefaultWithoutWhenNull_ImpliesDefaultPolicy()
    {
        var flow = Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              value: { $column: A, $default: n/a }
            output: { path: ./out }
            """).Flow;
        var root = Assert.IsType<TranslateObjectNode>(flow.Template);
        var leaf = Assert.IsType<TranslateValueNode>(root.Properties[0].Value);
        Assert.Equal(TranslateNullPolicy.Default, leaf.WhenNull);
        Assert.Equal("\"n/a\"", leaf.DefaultJson);
    }

    [Fact]
    public void Parse_PlainTypePropertyName_IsNotADirective()
    {
        // GeoJSON's 'type' is an ordinary property; only '$'-prefixed keys are dialect.
        var flow = Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              SpatialLocation:
                type: FeatureCollection
                features: []
            output: { path: ./out }
            """).Flow;
        var root = Assert.IsType<TranslateObjectNode>(flow.Template);
        var spatial = Assert.IsType<TranslateObjectNode>(root.Properties[0].Value);
        Assert.Equal("type", spatial.Properties[0].Name);
        var typeLeaf = Assert.IsType<TranslateValueNode>(spatial.Properties[0].Value);
        Assert.Equal("\"FeatureCollection\"", typeLeaf.ConstantJson);
        Assert.IsType<TranslateListNode>(spatial.Properties[1].Value);
    }

    [Fact]
    public void Parse_UrlTokensWithJsonLinesOutput_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template: { value: "{A}" }
            output: { path: ./out, mode: jsonLines }
            invoke:
              url: "https://api.example.com/records/{A}"
            """));
        Assert.Contains("invoke.url' uses {Column} tokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BatchSizeWithArrayOutput_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template: { value: "{A}" }
            output: { path: ./out, mode: array }
            invoke:
              url: https://api.example.com/records
              batchSize: 25
            """));
        Assert.Contains("does not apply to 'output.mode: array'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FileNameTokensWithSingleFileOutput_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template: { value: "{A}" }
            output: { path: ./out, mode: jsonLines, fileName: "doc_{A}" }
            """));
        Assert.Contains("output.fileName' uses {Column} tokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IndentOnJsonLines_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template: { value: "{A}" }
            output: { path: ./out, mode: jsonLines, indent: true }
            """));
        Assert.Contains("output.indent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SkipStatusCode2xx_FailsWithInvokePath()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template: { value: "{A}" }
            output: { path: ./out }
            invoke:
              url: https://api.example.com/records
              reliability: { skipStatusCodes: [200] }
            """));
        Assert.Contains("invoke.reliability.skipStatusCodes", ex.Message, StringComparison.Ordinal);
    }

    // --- Header/repeater: bind-less (single-instance) datasets and the $row directive -----------------------

    [Fact]
    public void Parse_BindlessDataset_IsSingleInstance()
    {
        var flow = Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: meta
                query: SELECT SYSUTCDATETIME() AS ExtractedUtc, COUNT(*) AS RecordCount FROM edw.Trip
            template:
              header:
                $row: meta
                $item:
                  extractedAt: { $column: ExtractedUtc, $type: dateTime }
                  recordCount: "{RecordCount}"
            output: { path: ./out }
            """).Flow;

        var dataset = Assert.Single(flow.Datasets);
        Assert.Empty(dataset.Bind);

        var header = Assert.IsType<TranslateObjectNode>(flow.Template).Properties.Single(p => p.Name == "header");
        var row = Assert.IsType<TranslateRowNode>(header.Value);
        Assert.Equal("meta", row.Row);
        Assert.IsType<TranslateObjectNode>(row.Item);
    }

    [Fact]
    public void Parse_RowOverBoundDataset_Compiles()
    {
        var flow = Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS OrderId }
            datasets:
              - name: shipping
                query: SELECT OrderId, Carrier FROM edw.Shipping
                bind: [OrderId]
            template:
              shipping:
                $row: shipping
                $item: { carrier: "{Carrier}" }
            output: { path: ./out }
            """).Flow;

        Assert.IsType<TranslateRowNode>(
            Assert.IsType<TranslateObjectNode>(flow.Template).Properties.Single().Value);
    }

    [Fact]
    public void Parse_RowAndForEachTogether_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: meta
                query: SELECT 1 AS A
            template:
              block: { $row: meta, $forEach: meta, $item: { a: "{A}" } }
            output: { path: ./out }
            """));
        Assert.Contains("both '$forEach' and '$row'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RowWithoutItem_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: meta
                query: SELECT 1 AS A
            template:
              block: { $row: meta }
            output: { path: ./out }
            """));
        Assert.Contains("requires '$item'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RowCombinedWithLeafDirective_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: meta
                query: SELECT 1 AS A
            template:
              block: { $row: meta, $item: { a: "{A}" }, $type: string }
            output: { path: ./out }
            """));
        Assert.Contains("combines '$row' with '$type'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RowOverUnknownDataset_ListsDeclared()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            datasets:
              - name: meta
                query: SELECT 1 AS A
            template:
              block: { $row: nope, $item: { a: "{A}" } }
            output: { path: ./out }
            """));
        Assert.Contains("'nope'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("meta", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RowOverPrimaryRows_RequiresResultSetGrain()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: trl
            name: t
            source: { connection: "${env:DWH}", query: SELECT 1 AS A }
            template:
              block: { $row: rows, $item: { a: "{A}" } }
            output: { path: ./out }
            """));
        Assert.Contains("documents.per: resultSet", ex.Message, StringComparison.Ordinal);
    }
}
