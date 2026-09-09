using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// The outbound Slack surface the notification pipeline needs: post a message, and resolve a user's
/// direct-message conversation from their email. Behind an interface so the pipeline and endpoints are testable
/// without Slack.
/// </summary>
public interface ISlackApiClient
{
    /// <summary>Posts a message to a channel or conversation id. <paramref name="blocksJson"/> is a Block Kit
    /// array; <paramref name="text"/> is the notification fallback either way.</summary>
    Task PostMessageAsync(string channel, string text, string? blocksJson, CancellationToken ct);

    /// <summary>The direct-message conversation id for the Slack account registered under
    /// <paramref name="email"/>. Throws a non-retryable <see cref="NotificationSendException"/> when no Slack
    /// account matches (a wrong address cannot be retried into existence).</summary>
    Task<string> ResolveDirectChannelAsync(string email, CancellationToken ct);
}

/// <summary>
/// Slack Web API over plain HTTP with the configured bot token (<c>chat:write</c>, plus <c>users:read.email</c>
/// and <c>im:write</c> for direct messages). The token is a secret reference resolved per call, so rotation needs
/// no restart. Email-to-DM resolutions are cached in-process for half a day: they are two API calls that
/// effectively never change, and re-resolving after a restart is harmless.
/// </summary>
public sealed class SlackApiClient : ISlackApiClient
{
    /// <summary>The named HttpClient this client uses (registered in Program).</summary>
    public const string HttpClientName = "sqlflow-slack";

    private static readonly TimeSpan DmCacheTtl = TimeSpan.FromHours(12);

    /// <summary>Slack error codes that no amount of retrying can fix: the destination itself is wrong.</summary>
    private static readonly HashSet<string> PermanentErrors = new(StringComparer.Ordinal)
    {
        "channel_not_found", "not_in_channel", "is_archived", "users_not_found", "user_not_found",
        "msg_too_long", "invalid_blocks", "invalid_blocks_format", "restricted_action", "cannot_dm_bot",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretResolver _secrets;
    private readonly TimeProvider _clock;
    private readonly SlackNotificationOptions _options;
    private readonly ConcurrentDictionary<string, (string ChannelId, DateTimeOffset CachedAt)> _dmCache =
        new(StringComparer.OrdinalIgnoreCase);

    public SlackApiClient(
        IHttpClientFactory httpClientFactory,
        ISecretResolver secrets,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _clock = clock;
        _options = options.Value.Notifications.Slack;
    }

    public async Task PostMessageAsync(string channel, string text, string? blocksJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var payload = new JsonObject
        {
            ["channel"] = channel,
            ["text"] = text,
            ["unfurl_links"] = false,
        };
        if (!string.IsNullOrWhiteSpace(blocksJson))
        {
            payload["blocks"] = ParseBlocks(blocksJson);
        }

        await CallAsync("chat.postMessage", payload, ct).ConfigureAwait(false);
    }

    public async Task<string> ResolveDirectChannelAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var now = _clock.GetUtcNow();
        if (_dmCache.TryGetValue(email, out var cached) && now - cached.CachedAt < DmCacheTtl)
        {
            return cached.ChannelId;
        }

        var lookup = await CallAsync(
            $"users.lookupByEmail?email={Uri.EscapeDataString(email)}", payload: null, ct).ConfigureAwait(false);
        var userId = lookup["user"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new NotificationSendException(
                $"Slack users.lookupByEmail returned no user id for '{email}'.", retryable: false);
        }

        var open = await CallAsync(
            "conversations.open", new JsonObject { ["users"] = userId }, ct).ConfigureAwait(false);
        var channelId = open["channel"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new NotificationSendException(
                $"Slack conversations.open returned no conversation id for the user behind '{email}'.", retryable: false);
        }

        _dmCache[email] = (channelId, now);
        return channelId;
    }

    /// <summary>One Slack Web API call: GET when <paramref name="payload"/> is null, JSON POST otherwise. Slack
    /// answers HTTP 200 with an <c>ok</c> envelope for API-level errors, and HTTP 429 for rate limits; both are
    /// mapped onto <see cref="NotificationSendException"/> with the right retry verdict.</summary>
    private async Task<JsonObject> CallAsync(string method, JsonObject? payload, CancellationToken ct)
    {
        var token = await _secrets.ResolveAsync(_options.BotTokenReference!, ct).ConfigureAwait(false);
        var url = _options.BaseUrl.TrimEnd('/') + "/" + method;
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NotificationSendException(
                $"the Slack {MethodName(method)} request failed to complete: {SecretHygiene.RedactedMessage(ex)}",
                retryable: true, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                throw new NotificationSendException(
                    $"Slack rate-limited {MethodName(method)}"
                    + (retryAfter is { } seconds ? $" (retry after {seconds:0}s)." : "."),
                    retryable: true);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new NotificationSendException(
                    $"Slack {MethodName(method)} returned HTTP {(int)response.StatusCode}: {SecretHygiene.RedactedMessage(Truncate(body))}",
                    retryable: (int)response.StatusCode >= 500);
            }

            JsonObject envelope;
            try
            {
                envelope = JsonNode.Parse(body) as JsonObject
                    ?? throw new NotificationSendException(
                        $"Slack {MethodName(method)} returned a non-object body.", retryable: true);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new NotificationSendException(
                    $"Slack {MethodName(method)} returned unparseable JSON: {ex.Message}", retryable: true, ex);
            }

            if (envelope["ok"]?.GetValue<bool>() is not true)
            {
                var error = envelope["error"]?.GetValue<string>() ?? "(no error code)";
                throw new NotificationSendException(
                    $"Slack {MethodName(method)} answered ok=false: {error}.",
                    retryable: !PermanentErrors.Contains(error));
            }

            return envelope;
        }
    }

    private static JsonNode ParseBlocks(string blocksJson)
    {
        try
        {
            return JsonNode.Parse(blocksJson)
                ?? throw new NotificationSendException("the composed Slack blocks JSON parsed to null.", retryable: false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Composed by NotificationComposer, so this indicates a composition bug: permanent by definition.
            throw new NotificationSendException(
                $"the composed Slack blocks JSON is invalid: {ex.Message}", retryable: false, ex);
        }
    }

    private static string MethodName(string method)
    {
        var query = method.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? method : method[..query];
    }

    private static string Truncate(string body) => body.Length > 500 ? body[..500] : body;
}
