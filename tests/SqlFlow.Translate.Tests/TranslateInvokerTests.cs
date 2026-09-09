using System.Net;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Translate;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Translate.Tests;

/// <summary>
/// The delivery step against a recording HTTP handler: what was saved is what goes on the wire, batching per
/// output layout, the OSDU-style records envelope, auth through the shared acquire surface, tolerated skip
/// statuses, and hard failure on anything else.
/// </summary>
public sealed class TranslateInvokerTests
{
    private static TranslateFlow Flow(string yamlTail) => new YamlTranslateFlowLoader().Parse($$"""
        flowType: trl
        name: t
        source:
          connection: ${env:DWH}
          query: SELECT 1
        template: { a: "{A}" }
        {{yamlTail}}
        """).Flow;

    private static TranslateInvoker Invoker(RecordingHandler handler, IReadOnlyDictionary<string, string>? secrets = null)
        => new(
            new MapSecretResolver(secrets ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            _ => new HttpClient(handler),
            TimeProvider.System);

    private static MemoryDestination WithFiles(params (string Path, string Content)[] files)
    {
        var destination = new MemoryDestination();
        foreach (var (path, content) in files)
        {
            destination.Files[path] = Encoding.UTF8.GetBytes(content);
        }

        return destination;
    }

    [Fact]
    public async Task FilePerDocument_PostsEachSavedFileVerbatim()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://api.example.com/records
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{\"a\":1}"), ("mem://out/t_2.json", "{\"a\":2}"));
        var handler = new RecordingHandler();

        var (sent, skipped) = await Invoker(handler).DeliverAsync(
            flow,
            [new TranslateSavedFile("mem://out/t_1.json", 1, null), new TranslateSavedFile("mem://out/t_2.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Equal(0, skipped);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("POST", r.Method));
        Assert.All(handler.Requests, r => Assert.Equal("application/json", r.ContentType));
        Assert.Contains(handler.Requests, r => r.Body == "{\"a\":1}");
        Assert.Contains(handler.Requests, r => r.Body == "{\"a\":2}");
    }

    [Fact]
    public async Task JsonLines_BatchesLinesIntoArrays()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: jsonLines }
            invoke:
              url: https://api.example.com/records
              batchSize: 2
            """);
        var destination = WithFiles(("mem://out/t.jsonl", "{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n"));
        var handler = new RecordingHandler();

        var (sent, _) = await Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t.jsonl", 3, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal(2, sent);
        Assert.Equal("[{\"a\":1},{\"a\":2}]", handler.Requests[0].Body);
        Assert.Equal("[{\"a\":3}]", handler.Requests[1].Body);
    }

    [Fact]
    public async Task EnvelopeKey_WrapsTheOsduRecordsShape()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://osdu.example.com/api/storage/v2/records
              method: PUT
              envelopeKey: records
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{\"kind\":\"osdu:wks:dataset--File.Generic:1.1.0\"}"));
        var handler = new RecordingHandler();

        await Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t_1.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("{\"records\":[{\"kind\":\"osdu:wks:dataset--File.Generic:1.1.0\"}]}", request.Body);
    }

    [Fact]
    public async Task ArrayOutput_PostsTheWholeSavedArrayOnce()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: array }
            invoke:
              url: https://api.example.com/records
              envelopeKey: records
            """);
        var destination = WithFiles(("mem://out/t.json", "[{\"a\":1},{\"a\":2}]"));
        var handler = new RecordingHandler();

        var (sent, _) = await Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t.json", 2, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal("{\"records\":[{\"a\":1},{\"a\":2}]}", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task UrlTokens_RenderPerDocumentFromTheManifestRow()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: "https://api.example.com/records/{A}"
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{\"a\":1}"));
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = "id 1" };
        var handler = new RecordingHandler();

        await Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t_1.json", 1, row)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        // The token value is URL-escaped.
        Assert.Equal("https://api.example.com/records/id%201", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task BearerAuthAndHeaders_ResolveSecretsAndApply()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://api.example.com/records
              headers:
                data-partition-id: kolumbus
                Content-Type: application/json; charset=utf-8
              auth:
                type: bearer
                secretRef: ${env:OSDU_TOKEN}
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{}"));
        var handler = new RecordingHandler();
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["${env:OSDU_TOKEN}"] = "token-123" };

        await Invoker(handler, secrets).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t_1.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer token-123", request.Headers["Authorization"]);
        Assert.Equal("kolumbus", request.Headers["data-partition-id"]);
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task SkipStatusCodes_TolerateSingleRequests()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://api.example.com/records
              reliability: { skipStatusCodes: [409] }
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{\"a\":1}"), ("mem://out/t_2.json", "{\"a\":2}"));
        var handler = new RecordingHandler(sent => sent.Body.Contains("\"a\":1", StringComparison.Ordinal)
            ? HttpStatusCode.Conflict
            : HttpStatusCode.OK);

        var (sent, skipped) = await Invoker(handler).DeliverAsync(
            flow,
            [new TranslateSavedFile("mem://out/t_1.json", 1, null), new TranslateSavedFile("mem://out/t_2.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal(1, sent);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public async Task UnlistedFailureStatus_FailsTheDelivery()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://api.example.com/records
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{\"a\":1}"));
        var handler = new RecordingHandler(_ => HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t_1.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None));
        Assert.Contains("API delivery failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroDocuments_SendsNothingForPerDocumentLayouts()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: jsonLines }
            invoke:
              url: https://api.example.com/records
            """);
        var destination = WithFiles(("mem://out/t.jsonl", string.Empty));
        var handler = new RecordingHandler();

        var (sent, skipped) = await Invoker(handler).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t.jsonl", 0, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal(0, sent);
        Assert.Equal(0, skipped);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApiKeyQueryAuth_AppendsTheQueryParameter()
    {
        var flow = Flow("""
            output: { path: mem://out, mode: filePerDocument }
            invoke:
              url: https://api.example.com/records?v=2
              auth:
                type: apiKeyQuery
                paramName: token
                secretRef: ${env:API_KEY}
            """);
        var destination = WithFiles(("mem://out/t_1.json", "{}"));
        var handler = new RecordingHandler();
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["${env:API_KEY}"] = "k-1" };

        await Invoker(handler, secrets).DeliverAsync(
            flow, [new TranslateSavedFile("mem://out/t_1.json", 1, null)],
            destination, NullRunEventSink.Instance, CancellationToken.None);

        Assert.Equal("https://api.example.com/records?v=2&token=k-1", Assert.Single(handler.Requests).Url);
    }
}
