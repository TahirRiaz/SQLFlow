using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Translate;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Translate.Tests;

/// <summary>
/// The renderer, driven through the real YAML compiler so a template that parses is proven to render: the
/// happy path reproduces the OSDU dataset--File.Generic example record (Examples/dataset in the OSDU
/// data-definitions repository) from flat SQL-shaped rows, and the focused cases pin the dialect's semantics
/// (typed passthrough, coercions, null policies, dataset binding, embedded JSON, scope shadowing).
/// </summary>
public sealed class JsonTemplateRendererTests
{
    private static TranslateFlow Compile(string yaml) => new YamlTranslateFlowLoader().Parse(yaml).Flow;

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] columns)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in columns)
        {
            row[name] = value;
        }

        return row;
    }

    [Fact]
    public void Render_OsduFileGenericRecord_MatchesTheExampleShape()
    {
        // The template mirrors the published File.Generic.1.1.0 example: envelope constants, ACL arrays from a
        // bound child dataset, a tags object, and the nested data.DatasetProperties.FileSourceInfo block.
        var flow = Compile("""
            flowType: trl
            name: osdu_dataset_01_trl
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: acl
                query: SELECT 1
                bind: [DatasetId]
            template:
              id: "namespace:dataset--File.Generic:{DatasetGuid}"
              kind: osdu:wks:dataset--File.Generic:1.1.0
              acl:
                owners:
                  $forEach: acl
                  $item: "{Owner}"
                viewers: [ "someone@company.com" ]
              legal:
                legaltags: [ "Example legaltags" ]
                otherRelevantDataCountries: [ "US" ]
                status: compliant
              tags:
                NameOfKey: String value
              ancestry:
                parents: []
              meta: []
              data:
                Source: Example Data Source
                Name: "{FileName}"
                Description: "{Description}"
                TotalSize: { $column: FileSizeBytes, $type: string }
                Endian: BIG
                DatasetProperties:
                  FileSourceInfo:
                    FileSource: "{FileSource}"
                    Name: "{FileName}"
                    FileSize: { $column: FileSizeBytes, $type: string }
                    PreloadFileCreateDate: { $column: CreatedUtc, $type: dateTime, $format: "yyyy-MM-dd'T'HH:mm:ss.fff'Z'" }
                    Checksum: "{Checksum}"
                Checksum: "{Checksum}"
                ExtensionProperties: {}
            output:
              path: ./out
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(
            flow.Datasets[0],
            ["DatasetId", "Owner"],
            [
                Row(("DatasetId", 42), ("Owner", "someone@company.com")),
                Row(("DatasetId", 43), ("Owner", "not-this-dataset@company.com")),
            ]);

        var row = Row(
            ("DatasetId", 42),
            ("DatasetGuid", "7c6c0a0d-9e6d-5087-bed9-106233ba57ea"),
            ("FileName", "1000.witsml"),
            ("Description", "As originally delivered by ACME.com."),
            ("FileSizeBytes", 42949672960L),
            ("FileSource", "s3://default_bucket/r1/data/provided/documents/1000.witsml"),
            ("CreatedUtc", new DateTime(2020, 12, 16, 11, 46, 20, 163, DateTimeKind.Utc)),
            ("Checksum", "d41d8cd98f00b204e9800998ecf8427e"));

        var document = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty.Push(row), index, flow.Nulls);

        Assert.NotNull(document);
        var root = document!.AsObject();
        Assert.Equal("namespace:dataset--File.Generic:7c6c0a0d-9e6d-5087-bed9-106233ba57ea", (string?)root["id"]);
        Assert.Equal("osdu:wks:dataset--File.Generic:1.1.0", (string?)root["kind"]);

        // The bound dataset selects only DatasetId 42's principals.
        var owners = root["acl"]!["owners"]!.AsArray();
        Assert.Single(owners);
        Assert.Equal("someone@company.com", (string?)owners[0]);
        Assert.Equal("compliant", (string?)root["legal"]!["status"]);
        Assert.Equal("String value", (string?)root["tags"]!["NameOfKey"]);
        Assert.Empty(root["ancestry"]!["parents"]!.AsArray());
        Assert.Empty(root["meta"]!.AsArray());

        var data = root["data"]!.AsObject();
        // OSDU carries sizes as strings; $type: string coerces the bigint.
        Assert.Equal("42949672960", (string?)data["TotalSize"]);
        var info = data["DatasetProperties"]!["FileSourceInfo"]!.AsObject();
        Assert.Equal("s3://default_bucket/r1/data/provided/documents/1000.witsml", (string?)info["FileSource"]);
        Assert.Equal("1000.witsml", (string?)info["Name"]);
        Assert.Equal("2020-12-16T11:46:20.163Z", (string?)info["PreloadFileCreateDate"]);
        Assert.Empty(data["ExtensionProperties"]!.AsObject());
    }

    private static TranslateFlow CompileTemplate(string templateBody, string extra = "")
        => Compile($$"""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            {{extra}}
            template:
            {{templateBody}}
            output:
              path: ./out
            """);

    [Fact]
    public void Render_SingleTokenLeaf_PassesNativeTypesThrough()
    {
        var flow = CompileTemplate("""
              count: "{N}"
              price: "{P}"
              active: "{B}"
              label: "n={N}"
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template,
            TranslateScope.Empty.Push(Row(("N", 7), ("P", 19.5m), ("B", true))),
            new TranslateDatasetIndex(), flow.Nulls)!.AsObject();

        // Whole-string tokens keep the native JSON type (integers normalize to long); a mixed template
        // renders as a string.
        Assert.Equal(7L, (long?)document["count"]);
        Assert.Equal(19.5m, (decimal?)document["price"]);
        Assert.Equal(true, (bool?)document["active"]);
        Assert.Equal("n=7", (string?)document["label"]);
    }

    [Fact]
    public void Render_NullHandling_OmitIncludeAndDefault()
    {
        var flow = CompileTemplate("""
              omitted: "{A}"
              included: { $column: A, $whenNull: "null" }
              defaulted: { $column: A, $default: n/a }
              present: "{B}"
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template,
            TranslateScope.Empty.Push(Row(("A", null), ("B", "x"))),
            new TranslateDatasetIndex(), flow.Nulls)!.AsObject();

        // documents.nulls defaults to omit, so the inherit leaf disappears; the explicit policies win locally.
        Assert.False(document.ContainsKey("omitted"));
        Assert.True(document.ContainsKey("included"));
        Assert.Null(document["included"]);
        Assert.Equal("n/a", (string?)document["defaulted"]);
        Assert.Equal("x", (string?)document["present"]);
    }

    [Fact]
    public void Render_NullsInclude_EmitsExplicitNulls()
    {
        var flow = CompileTemplate("""
              a: "{A}"
            """, extra: """
            documents: { nulls: include }
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("A", null))), new TranslateDatasetIndex(), flow.Nulls)!.AsObject();
        Assert.True(document.ContainsKey("a"));
        Assert.Null(document["a"]);
    }

    [Fact]
    public void Render_TemplateWithAllTokensNull_FollowsTheNullPolicy()
    {
        var flow = CompileTemplate("""
              id: "urn:thing:{A}"
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("A", null))), new TranslateDatasetIndex(), flow.Nulls)!.AsObject();

        // Emitting the bare literal skeleton "urn:thing:" would fabricate an identifier; the policy applies.
        Assert.False(document.ContainsKey("id"));
    }

    [Fact]
    public void Render_EmbeddedJsonColumn_ParsesIntoTheDocument()
    {
        var flow = CompileTemplate("""
              SpatialLocation: { $column: GeoJson, $type: json }
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template,
            TranslateScope.Empty.Push(Row(("GeoJson", """{"type":"Point","coordinates":[5.73,58.97]}"""))),
            new TranslateDatasetIndex(), flow.Nulls)!.AsObject();

        Assert.Equal("Point", (string?)document["SpatialLocation"]!["type"]);
        Assert.Equal(5.73, (double?)document["SpatialLocation"]!["coordinates"]![0]);
    }

    [Fact]
    public void Render_InvalidEmbeddedJson_FailsWithThePath()
    {
        var flow = CompileTemplate("""
              SpatialLocation: { $column: GeoJson, $type: json }
            """);
        var ex = Assert.Throws<SqlFlowException>(() => JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("GeoJson", "not json"))), new TranslateDatasetIndex(), flow.Nulls));
        Assert.Contains("$.SpatialLocation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_MissingColumn_ListsTheColumnsInScope()
    {
        var flow = CompileTemplate("""
              a: "{Missing}"
            """);
        var ex = Assert.Throws<SqlFlowException>(() => JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("A", 1), ("B", 2))), new TranslateDatasetIndex(), flow.Nulls));
        Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("A, B", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NestedForEach_BindsThroughTheScopeChain()
    {
        // wells -> wellbores (bound by WellId) -> markers (bound by WellboreId from the wellbore row).
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: wellbores
                query: SELECT 1
                bind: [WellId]
              - name: markers
                query: SELECT 1
                bind: [WellboreId]
            template:
              WellName: "{WellName}"
              Wellbores:
                $forEach: wellbores
                $item:
                  Name: "{WellboreName}"
                  Well: "{WellName}"
                  Markers:
                    $forEach: markers
                    $item: "{MarkerName}"
            output:
              path: ./out
            """);

        var index = new TranslateDatasetIndex();
        index.AddDataset(flow.Datasets[0], ["WellId", "WellboreId", "WellboreName"],
        [
            Row(("WellId", 1), ("WellboreId", 10), ("WellboreName", "WB-10")),
            Row(("WellId", 1), ("WellboreId", 11), ("WellboreName", "WB-11")),
            Row(("WellId", 2), ("WellboreId", 20), ("WellboreName", "WB-20")),
        ]);
        index.AddDataset(flow.Datasets[1], ["WellboreId", "MarkerName"],
        [
            Row(("WellboreId", 10), ("MarkerName", "Top")),
            Row(("WellboreId", 10), ("MarkerName", "Base")),
            Row(("WellboreId", 11), ("MarkerName", "Mid")),
        ]);

        var document = JsonTemplateRenderer.Render(
            flow.Template,
            TranslateScope.Empty.Push(Row(("WellId", 1), ("WellName", "W-1"))),
            index, flow.Nulls)!.AsObject();

        var wellbores = document["Wellbores"]!.AsArray();
        Assert.Equal(2, wellbores.Count);
        // The item template still sees the enclosing well row (scope chaining).
        Assert.Equal("W-1", (string?)wellbores[0]!["Well"]);
        var markers = wellbores[0]!["Markers"]!.AsArray();
        Assert.Equal(["Top", "Base"], markers.Select(m => (string?)m));
        Assert.Single(wellbores[1]!["Markers"]!.AsArray());
    }

    [Fact]
    public void Render_BindKeys_MatchAcrossNumericWidthsAndStringCase()
    {
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            datasets:
              - name: children
                query: SELECT 1
                bind: [Key]
            template:
              items:
                $forEach: children
                $item: "{V}"
            output:
              path: ./out
            """);

        var index = new TranslateDatasetIndex();
        // The dataset key is a bigint and lowercase; the parent row carries int and uppercase.
        index.AddDataset(flow.Datasets[0], ["Key", "V"], [Row(("Key", 7L), ("V", "seven"))]);
        var byNumber = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("Key", 7))), index, flow.Nulls)!.AsObject();
        Assert.Single(byNumber["items"]!.AsArray());

        var textIndex = new TranslateDatasetIndex();
        textIndex.AddDataset(flow.Datasets[0], ["Key", "V"], [Row(("Key", "abc"), ("V", "text"))]);
        var byText = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("Key", "ABC"))), textIndex, flow.Nulls)!.AsObject();
        Assert.Single(byText["items"]!.AsArray());
    }

    [Fact]
    public void Render_ResultSetGrain_IteratesThePrimaryRows()
    {
        var flow = Compile("""
            flowType: trl
            name: t
            source:
              connection: ${env:DWH}
              query: SELECT 1
            documents: { per: resultSet }
            template:
              records:
                $forEach: rows
                $item:
                  name: "{Name}"
            output:
              path: ./out
              mode: array
            """);

        var index = new TranslateDatasetIndex();
        index.SetPrimaryRows([Row(("Name", "a")), Row(("Name", "b"))]);
        var document = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty, index, flow.Nulls)!.AsObject();
        Assert.Equal(2, document["records"]!.AsArray().Count);
    }

    [Fact]
    public void Render_ConstantsAreFreshPerDocument()
    {
        // A JsonNode has a single parent; rendering the same template twice must not fail or share nodes.
        var flow = CompileTemplate("""
              legal:
                legaltags: [ "tag-a" ]
            """);
        var index = new TranslateDatasetIndex();
        var first = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty.Push(Row(("A", 1))), index, flow.Nulls);
        var second = JsonTemplateRenderer.Render(flow.Template, TranslateScope.Empty.Push(Row(("A", 2))), index, flow.Nulls);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(first!.ToJsonString(), second!.ToJsonString());
    }

    [Fact]
    public void Render_SecretReferencePassesThroughVerbatim()
    {
        var flow = CompileTemplate("""
              connection: "${env:SECRET}/{A}"
            """);
        var document = JsonTemplateRenderer.Render(
            flow.Template, TranslateScope.Empty.Push(Row(("A", "x"))), new TranslateDatasetIndex(), flow.Nulls)!.AsObject();
        Assert.Equal("${env:SECRET}/x", (string?)document["connection"]);
    }
}
