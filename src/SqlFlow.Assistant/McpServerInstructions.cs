using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SqlFlow.Assistant;

/// <summary>
/// The MCP server's own instructions, the one definition of how the assistant behaves on every surface: what a <c>!</c>
/// question is, how a conversation's mode works, which tool answers which question, and how a business answer is laid
/// out. The model providers' MCP connectors forward the server's tools but not its instructions, so a gateway reads them
/// from the server's <c>initialize</c> answer and gives them to the model as the system prompt, with only what its
/// surface renders added (<see cref="AssistantInstructions"/>). The server gives a session the instructions of the mode
/// its address names, describing only the tools that mode lists, so they are read at the same address a run's session
/// opens in that mode, and each mode's are kept for <see cref="CacheFor"/>, so a redeployed server is read again soon
/// without a round trip on every question.
/// </summary>
public sealed class McpServerInstructions : IDisposable
{
    /// <summary>How long one read of the instructions is used before the server is asked again.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>The MCP protocol version this client asks for; the server answers with the one it speaks.</summary>
    private const string ProtocolVersion = "2025-03-26";

    /// <summary>The header a streamable HTTP server names the session it opened with.</summary>
    private const string SessionHeader = "Mcp-Session-Id";

    private readonly AssistantSettings _settings;
    private readonly ILogger<McpServerInstructions> _logger;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<AssistantMode, (string Text, DateTimeOffset ReadAt)> _cached = [];

    public McpServerInstructions(AssistantSettings settings, ILogger<McpServerInstructions> logger)
        : this(settings, logger, TimeProvider.System, new HttpClientHandler())
    {
    }

    /// <summary>The reader over <paramref name="handler"/>, read by <paramref name="clock"/>: what tests drive.</summary>
    public McpServerInstructions(
        AssistantSettings settings, ILogger<McpServerInstructions> logger, TimeProvider clock, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handler);
        _settings = settings;
        _logger = logger;
        _clock = clock;
        // One question's run bounds the read through its own token; this ceiling only stops a server that never answers.
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// The server's instructions for a session in <paramref name="mode"/>, read with <paramref name="bearer"/> (the run's
    /// own credential, which the MCP server checks before it answers anything) when that mode's last read is older than
    /// <see cref="CacheFor"/>. Concurrent questions share one read. A server that cannot be read fails the question: its
    /// tools would be unreachable too.
    /// </summary>
    public async Task<string> GetAsync(AssistantMode mode, string bearer, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        if (Fresh(mode) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(mode) is { } readMeanwhile)
            {
                return readMeanwhile;
            }
            var text = await ReadAsync(mode, bearer, ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _cached[mode] = (text, _clock.GetUtcNow());
            }
            return text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The instructions read for <paramref name="mode"/>, while that read is younger than <see cref="CacheFor"/>.
    /// Looked up under the lock every write takes, outside the read gate, so a fresh entry costs no wait; a stale or
    /// missing one sends the caller to the gated read.</summary>
    private string? Fresh(AssistantMode mode)
    {
        lock (_cacheLock)
        {
            return _cached.TryGetValue(mode, out var cached) && _clock.GetUtcNow() - cached.ReadAt < CacheFor
                ? cached.Text
                : null;
        }
    }

    /// <summary>One <c>initialize</c> exchange in <paramref name="mode"/>: the instructions out of its answer, then the
    /// session it opened closed.</summary>
    private async Task<string> ReadAsync(AssistantMode mode, string bearer, CancellationToken ct)
    {
        // The address a run's own session opens in this mode, so the instructions describe the tools that session lists.
        var address = _settings.Mcp.AssistantServerUri(mode);
        var initialize = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "sqlflow-assistant", ["version"] = "1" },
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(initialize.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var session = response.Headers.TryGetValues(SessionHeader, out var ids) ? ids.FirstOrDefault() : null;
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The MCP server at {address.GetLeftPart(UriPartial.Path)} refused to start a session for the assistant's "
                    + $"instructions: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var answer = await ReadAnswerAsync(response, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"The MCP server at {address.GetLeftPart(UriPartial.Path)} answered initialize without a JSON-RPC result.");
            var instructions = answer["result"]?["instructions"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(instructions))
            {
                throw new InvalidOperationException(
                    $"The MCP server at {address.GetLeftPart(UriPartial.Path)} gave no instructions in its initialize answer, "
                    + "so the assistant has no rules to follow.");
            }
            return instructions;
        }
        finally
        {
            if (session is not null)
            {
                await CloseAsync(address, bearer, session).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The JSON-RPC message answering request 1: the whole body when the server answered with JSON, else the
    /// first data line of the event stream that holds it.</summary>
    private static async Task<JsonNode?> ReadAnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var stream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
        if (!stream)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Answering(body);
        }

        using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(content, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal) && Answering(line[5..]) is { } answer)
            {
                return answer;
            }
        }
        return null;
    }

    /// <summary>The message in <paramref name="text"/> when it is the answer to request 1; null for anything else (a
    /// notification, a keep-alive, text that is not JSON).</summary>
    private static JsonNode? Answering(string text)
    {
        try
        {
            var message = JsonNode.Parse(text.Trim());
            return message?["id"]?.GetValueKind() == JsonValueKind.Number && message["id"]!.GetValue<int>() == 1
                ? message
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ends the session the read opened, so the server does not hold it until it expires. A failure costs the
    /// server an idle session, never the question, so it is logged rather than thrown.</summary>
    private async Task CloseAsync(Uri address, string bearer, string session)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add(SessionHeader, session);
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _http.SendAsync(request, closing.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("The MCP server answered {Status} when the instructions' session was closed",
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Closing the MCP session the instructions were read in failed");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
