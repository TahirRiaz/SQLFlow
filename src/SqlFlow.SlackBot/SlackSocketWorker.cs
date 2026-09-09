using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackNet;
using SlackNet.Events;
using SqlFlow.Assistant;

namespace SqlFlow.SlackBot;

/// <summary>
/// The hosted service that keeps the Socket Mode connection alive. Socket Mode means the bot
/// dials out to Slack over a websocket: no public inbound endpoint, so the bot can run next to
/// the control plane on a private network. SlackNet handles reconnection and envelope acking;
/// this worker wires the handler, connects, and holds until shutdown.
/// </summary>
public sealed class SlackSocketWorker : BackgroundService
{
    private readonly SlackBotOptions _options;
    private readonly IAssistantGateway _gateway;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SlackSocketWorker> _logger;

    public SlackSocketWorker(
        IOptions<SlackBotOptions> options,
        IAssistantGateway gateway,
        ILoggerFactory loggerFactory,
        ILogger<SlackSocketWorker> logger)
    {
        _options = options.Value;
        _gateway = gateway;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slack = new SlackServiceBuilder()
            .UseApiToken(_options.Slack.BotToken)
            .UseAppLevelToken(_options.Slack.AppToken);

        var handler = new SlackAssistantHandler(
            slack.GetApiClient(),
            _gateway,
            _options,
            _loggerFactory.CreateLogger<SlackAssistantHandler>());
        slack.RegisterEventHandler<AppMention>(ctx => handler);
        slack.RegisterEventHandler<MessageEvent>(ctx => handler);

        using var socket = slack.GetSocketModeClient();
        await ConnectWithRetryAsync(socket, stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("Connected to Slack over Socket Mode; answering mentions and DMs");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the using disposes the socket, which disconnects cleanly.
        }
    }

    /// <summary>Slack error codes that mean the configuration is wrong and no retry can help.</summary>
    private static readonly string[] FatalSlackErrors =
        ["invalid_auth", "not_authed", "account_inactive", "token_revoked", "token_expired", "invalid_app"];

    /// <summary>
    /// Establishes the initial connection with exponential backoff, because at container start
    /// the network (VNet, DNS, egress) is routinely not ready yet. Bad credentials fail fast:
    /// retrying an invalid token forever would just hide a deployment error. Once connected,
    /// SlackNet's own reconnection takes over for the life of the process.
    /// </summary>
    private async Task ConnectWithRetryAsync(ISlackSocketModeClient socket, CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromSeconds(60);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await socket.Connect(cancellationToken: stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (SlackException ex) when (FatalSlackErrors.Contains(ex.ErrorCode, StringComparer.Ordinal))
            {
                _logger.LogCritical("Slack rejected the configured tokens ({Error}); fix SlackBot:Slack:AppToken / BotToken", ex.ErrorCode);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Slack Socket Mode connect attempt {Attempt} failed; retrying in {Delay}s", attempt, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }
}
