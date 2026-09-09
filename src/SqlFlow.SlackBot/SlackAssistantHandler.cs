using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;
using SqlFlow.Assistant;

namespace SqlFlow.SlackBot;

/// <summary>
/// Handles the two ways users reach the bot: an @-mention in a channel (<see cref="AppMention"/>)
/// and a direct message (<see cref="MessageEvent"/> with channel type <c>im</c>). Each question is
/// acknowledged with an eyes reaction, answered by the configured model provider in the context of
/// its Slack thread, and the answer posted as a threaded reply. Processing is offloaded so the
/// Socket Mode event loop is never blocked by a long agent run.
/// </summary>
public sealed partial class SlackAssistantHandler : IEventHandler<AppMention>, IEventHandler<MessageEvent>, IDisposable
{
    private readonly ISlackApiClient _slack;
    private readonly IAssistantGateway _gateway;
    private readonly SlackBotOptions _options;
    private readonly ILogger<SlackAssistantHandler> _logger;

    /// <summary>The bot's own user id, resolved once from auth.test: used to ignore our own
    /// messages and to strip our mention from question text.</summary>
    private readonly Lazy<Task<string>> _selfUserId;

    /// <summary>
    /// Recently handled message keys (channel:ts). A mention in a DM arrives as both an
    /// app_mention and a message event, and Slack redelivers events it thinks were not acked in
    /// time; both must not produce a second answer.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _handled = new(StringComparer.Ordinal);

    /// <summary>Caps concurrent agent runs; a burst of questions queues behind this instead of
    /// stampeding the model deployment. Questions are acknowledged before they queue.</summary>
    private readonly SemaphoreSlim _concurrency;

    /// <summary>Downloads image attachments from Slack's private file URLs. The bot token is the default
    /// Authorization header because every such URL requires it (the files:read scope).</summary>
    private readonly HttpClient _http;

    [GeneratedRegex("<@[A-Z0-9]+>")]
    private static partial Regex MentionRegex();

    public SlackAssistantHandler(
        ISlackApiClient slack,
        IAssistantGateway gateway,
        SlackBotOptions options,
        ILogger<SlackAssistantHandler> logger)
    {
        _slack = slack;
        _gateway = gateway;
        _options = options;
        _logger = logger;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentAnswers, options.MaxConcurrentAnswers);
        _selfUserId = new Lazy<Task<string>>(async () => (await _slack.Auth.Test().ConfigureAwait(false)).UserId);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.Slack.BotToken);
    }

    public void Dispose()
    {
        _concurrency.Dispose();
        _http.Dispose();
    }

    public Task Handle(AppMention slackEvent)
    {
        Dispatch(slackEvent.Channel, slackEvent.User, slackEvent.Text, slackEvent.Ts, slackEvent.ThreadTs, slackEvent.Files?.ToList());
        return Task.CompletedTask;
    }

    public async Task Handle(MessageEvent slackEvent)
    {
        // Only plain user DMs: channel traffic is handled via app_mention, and edits, joins, and
        // bot posts (including our own answers) carry a subtype or a bot message type.
        if (slackEvent.ChannelType != "im" || slackEvent is BotMessage || slackEvent.Subtype is not null)
        {
            return;
        }
        var self = await _selfUserId.Value.ConfigureAwait(false);
        if (slackEvent.User == self || string.IsNullOrEmpty(slackEvent.User))
        {
            return;
        }
        Dispatch(slackEvent.Channel, slackEvent.User, slackEvent.Text, slackEvent.Ts, slackEvent.ThreadTs, slackEvent.Files?.ToList());
    }

    /// <summary>Dedupes, then runs the full question pipeline on the thread pool. Fire-and-forget
    /// by design: the Socket Mode loop must ack promptly, and every failure path inside ends in a
    /// Slack error reply plus a log line, never an unobserved exception.</summary>
    private void Dispatch(string channel, string user, string text, string ts, string? threadTs, IReadOnlyList<SlackNet.File>? files)
    {
        var key = $"{channel}:{ts}";
        var now = DateTimeOffset.UtcNow;
        if (!_handled.TryAdd(key, now))
        {
            return;
        }
        PruneHandled(now);

        _ = Task.Run(() => AnswerAsync(channel, user, text, ts, threadTs ?? ts, files ?? []));
    }

    private void PruneHandled(DateTimeOffset now)
    {
        if (_handled.Count <= 500)
        {
            return;
        }
        foreach (var entry in _handled)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(15))
            {
                _handled.TryRemove(entry.Key, out _);
            }
        }
    }

    private async Task AnswerAsync(string channel, string user, string text, string ts, string threadTs, IReadOnlyList<SlackNet.File> files)
    {
        var gated = false;
        try
        {
            await AcknowledgeAsync(channel, ts).ConfigureAwait(false);

            var question = MentionRegex().Replace(text ?? "", "").Trim();
            var images = await DownloadImagesAsync(files).ConfigureAwait(false);
            if (question.Length == 0 && images.Count == 0)
            {
                await PostAsync(channel, threadTs,
                    "Ask me anything about SQLFlow: pipeline and run status, why a run failed, " +
                    "what a table contains, lineage, schedules, or how a `.flow.yaml` key works. " +
                    "You can also paste a screenshot of an error and ask about it.")
                    .ConfigureAwait(false);
                return;
            }
            // An image with no words is still a question ("what is this error?"): give the model a prompt.
            if (question.Length == 0)
            {
                question = "Look at the attached image and help me understand or resolve what it shows.";
            }

            var priorTurns = await PriorTurnsAsync(channel, ts, threadTs).ConfigureAwait(false);

            await _concurrency.WaitAsync().ConfigureAwait(false);
            gated = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RunTimeoutSeconds + 30));
            var answer = await _gateway.AskAsync(new AssistantRequest
            {
                ConversationKey = $"{channel}:{threadTs}",
                PriorTurns = priorTurns,
                Question = question,
                ImageDataUris = images,
                // The MCP server forwards this verbatim to the control plane, which enforces the
                // token's scopes; the read-scoped bot token is the whole authority of every run.
                McpBearer = _options.SqlFlow.AccessToken,
            }, timeout.Token).ConfigureAwait(false);

            await PostAsync(channel, threadTs, SlackMrkdwn.FromMarkdown(answer)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to answer {Channel}:{Ts} from {User}", channel, ts, user);
            var reason = ex is TimeoutException
                ? "the answer took too long and was cancelled"
                : "something went wrong while answering";
            try
            {
                await PostAsync(channel, threadTs, $":warning: Sorry, {reason}. Please try again; if it keeps failing, ask an admin to check the SQLFlow Slack bot logs.")
                    .ConfigureAwait(false);
            }
            catch (Exception postEx)
            {
                _logger.LogError(postEx, "Could not post the error reply to {Channel}:{Ts}", channel, threadTs);
            }
        }
        finally
        {
            if (gated)
            {
                _concurrency.Release();
            }
        }
    }

    /// <summary>Best-effort eyes reaction so the user sees the question was picked up. A missing
    /// reactions:write scope or a duplicate reaction must never fail the answer.</summary>
    private async Task AcknowledgeAsync(string channel, string ts)
    {
        try
        {
            await _slack.Reactions.AddToMessage("eyes", channel, ts).ConfigureAwait(false);
        }
        catch (SlackException ex)
        {
            _logger.LogDebug("Could not add the acknowledgement reaction in {Channel}: {Error}", channel, ex.Message);
        }
    }

    /// <summary>
    /// The Slack-thread transcript before the current message, oldest first, for rebuilding the Foundry
    /// conversation after a restart. A top-level mention is answered on its own (no thread, so no prior
    /// turns): the bot only takes surrounding context from a thread it is invoked inside, never from the
    /// broader channel. A follow-up in a thread fetches that thread's replies, the durable transcript.
    /// </summary>
    private async Task<IReadOnlyList<ConversationTurn>> PriorTurnsAsync(string channel, string ts, string threadTs)
    {
        if (threadTs == ts)
        {
            return [];
        }
        var self = await _selfUserId.Value.ConfigureAwait(false);

        // conversations.replies pages oldest-first; walk the cursor so a long thread is seen in full,
        // capped well past what is ever replayed.
        const int maxFetched = 500;
        var messages = new List<SlackNet.Events.MessageEvent>();
        string? cursor = null;
        do
        {
            var page = await _slack.Conversations.Replies(channel, threadTs, limit: 200, cursor: cursor).ConfigureAwait(false);
            messages.AddRange(page.Messages);
            cursor = page.HasMore ? page.ResponseMetadata?.NextCursor : null;
        }
        while (!string.IsNullOrEmpty(cursor) && messages.Count < maxFetched);

        return messages
            .Where(m => m.Ts != ts && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ConversationTurn(
                m is BotMessage || m.User == self,
                MentionRegex().Replace(m.Text, "").Trim()))
            .Where(t => t.Text.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Downloads the message's image attachments and returns them as data URIs for the model's vision
    /// input. Non-image files are ignored; an image past <see cref="SlackBotOptions.MaxImageBytes"/> or
    /// beyond <see cref="SlackBotOptions.MaxImages"/> is skipped (a download failure never fails the
    /// answer, the question is still answered from its text). The bot token authorizes each private URL.
    /// </summary>
    private async Task<IReadOnlyList<string>> DownloadImagesAsync(IReadOnlyList<SlackNet.File> files)
    {
        if (files.Count == 0 || _options.MaxImages <= 0)
        {
            return [];
        }

        var images = new List<string>();
        foreach (var file in files)
        {
            if (images.Count >= _options.MaxImages)
            {
                break;
            }
            var mime = file.Mimetype ?? "";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (file.Size > _options.MaxImageBytes)
            {
                _logger.LogInformation("Skipping image {Name} ({Size} bytes > {Max} cap)", file.Name, file.Size, _options.MaxImageBytes);
                continue;
            }
            var url = file.UrlPrivateDownload ?? file.UrlPrivate;
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }
            try
            {
                var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length == 0 || bytes.Length > _options.MaxImageBytes)
                {
                    continue;
                }
                images.Add($"data:{mime};base64,{Convert.ToBase64String(bytes)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // A missing files:read scope, a revoked URL, or a slow download must not sink the answer;
                // note it and answer from the text alone.
                _logger.LogWarning("Could not download Slack image {Name}: {Error}", file.Name, ex.Message);
            }
        }
        return images;
    }

    /// <summary>Posts with a short retry on Slack rate limiting (chat.postMessage is limited to
    /// roughly one message per second per channel); other Slack errors surface immediately.</summary>
    private async Task PostAsync(string channel, string threadTs, string text)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _slack.Chat.PostMessage(new Message
                {
                    Channel = channel,
                    ThreadTs = threadTs,
                    Text = text,
                    UnfurlLinks = false,
                }).ConfigureAwait(false);
                return;
            }
            catch (SlackException ex) when (attempt < maxAttempts && ex.ErrorCode is "ratelimited" or "rate_limited")
            {
                _logger.LogWarning("Rate limited posting to {Channel}; retrying (attempt {Attempt}/{Max})", channel, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt)).ConfigureAwait(false);
            }
        }
    }
}
