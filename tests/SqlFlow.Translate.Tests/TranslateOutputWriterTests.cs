using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Translate;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Translate.Tests;

/// <summary>The output writer's three layouts, the deterministic-overwrite naming, and the empty-result files.</summary>
public sealed class TranslateOutputWriterTests
{
    private static readonly DateTime StartUtc = new(2026, 8, 15, 4, 0, 0, DateTimeKind.Utc);

    private static TranslateFlow Flow(string outputTail) => new YamlTranslateFlowLoader().Parse($$"""
        flowType: trl
        name: osdu_dataset_01_trl
        source:
          connection: ${env:DWH}
          query: SELECT 1
        template: { a: "{A}" }
        output:
          path: mem://out
        {{outputTail}}
        """).Flow;

    private static Dictionary<string, object?> Row(string name, object? value)
        => new(StringComparer.OrdinalIgnoreCase) { [name] = value };

    private static JsonNode Doc(int n) => JsonNode.Parse($$"""{"a":{{n}}}""")!;

    [Fact]
    public async Task JsonLines_WritesOneCompactLinePerDocument()
    {
        var destination = new MemoryDestination();
        var flow = Flow("  mode: jsonLines");
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        await writer.WriteAsync(Doc(2), Row("A", 2), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);

        var file = Assert.Single(writer.Files);
        Assert.Equal("mem://out/osdu_dataset_01_trl.jsonl", file.Path);
        Assert.Equal(2, file.Rows);
        Assert.Equal("{\"a\":1}\n{\"a\":2}\n", destination.Text(file.Path));
        Assert.Equal(destination.Files[file.Path].LongLength, file.Bytes);
    }

    [Fact]
    public async Task Array_WritesOneJsonArrayFile()
    {
        var destination = new MemoryDestination();
        var flow = Flow("  mode: array");
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        await writer.WriteAsync(Doc(2), Row("A", 2), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);

        var file = Assert.Single(writer.Files);
        Assert.Equal("mem://out/osdu_dataset_01_trl.json", file.Path);
        Assert.Equal("[{\"a\":1},{\"a\":2}]", destination.Text(file.Path));
    }

    [Fact]
    public async Task FilePerDocument_RendersTokenNamesAndKeepsRowsForTheManifest()
    {
        var destination = new MemoryDestination();
        var flow = Flow("""
              mode: filePerDocument
              fileName: "dataset_{A}"
            """);
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: true);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        await writer.WriteAsync(Doc(2), Row("A", 2), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);

        Assert.Equal(2, writer.Files.Count);
        Assert.Equal("mem://out/dataset_1.json", writer.Files[0].Path);
        Assert.Equal("mem://out/dataset_2.json", writer.Files[1].Path);
        Assert.Equal("{\"a\":2}", destination.Text("mem://out/dataset_2.json"));
        Assert.All(writer.Manifest, entry => Assert.NotNull(entry.Row));
    }

    [Fact]
    public async Task FilePerDocument_TokenlessNameGetsTheDocumentNumber()
    {
        var destination = new MemoryDestination();
        var flow = Flow("  mode: filePerDocument");
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        await writer.WriteAsync(Doc(2), Row("A", 2), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);

        Assert.Equal("mem://out/osdu_dataset_01_trl_1.json", writer.Files[0].Path);
        Assert.Equal("mem://out/osdu_dataset_01_trl_2.json", writer.Files[1].Path);
    }

    [Fact]
    public async Task FilePerDocument_DuplicateRenderedName_Fails()
    {
        var destination = new MemoryDestination();
        var flow = Flow("""
              mode: filePerDocument
              fileName: "dataset_{A}"
            """);
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => writer.WriteAsync(Doc(2), Row("A", 1), CancellationToken.None));
        Assert.Contains("rendered the same name twice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilePerDocument_HostileTokenValues_AreSanitized()
    {
        var destination = new MemoryDestination();
        var flow = Flow("""
              mode: filePerDocument
              fileName: "dataset_{A}"
            """);
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", "urn:x/../y"), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
        Assert.Equal("mem://out/dataset_urn_x_.._y.json", Assert.Single(writer.Files).Path);
    }

    [Fact]
    public async Task EmptyResult_StillWritesTheSingleFileTruth()
    {
        var destination = new MemoryDestination();

        var jsonl = Flow("  mode: jsonLines");
        await using (var writer = new TranslateOutputWriter(jsonl, destination, StartUtc, keepRows: false))
        {
            await writer.CompleteAsync(CancellationToken.None);
            var file = Assert.Single(writer.Files);
            Assert.Equal(0, file.Rows);
            Assert.Equal(string.Empty, destination.Text(file.Path));
        }

        var array = Flow("  mode: array");
        await using (var writer = new TranslateOutputWriter(array, destination, StartUtc, keepRows: false))
        {
            await writer.CompleteAsync(CancellationToken.None);
            Assert.Equal("[]", destination.Text(Assert.Single(writer.Files).Path));
        }
    }

    [Fact]
    public async Task AddTimestamp_SuffixesTheSingleFileName()
    {
        var destination = new MemoryDestination();
        var flow = Flow("  addTimestamp: true");
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(Doc(1), Row("A", 1), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
        Assert.Equal("mem://out/osdu_dataset_01_trl_20260815040000.jsonl", Assert.Single(writer.Files).Path);
    }

    [Fact]
    public async Task NullDocument_SerializesAsJsonNull()
    {
        var destination = new MemoryDestination();
        var flow = Flow("  mode: jsonLines");
        await using var writer = new TranslateOutputWriter(flow, destination, StartUtc, keepRows: false);
        await writer.WriteAsync(null, Row("A", null), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
        Assert.Equal("null\n", destination.Text(Assert.Single(writer.Files).Path));
    }
}
