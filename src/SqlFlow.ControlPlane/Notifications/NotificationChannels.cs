using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// One outbound channel the send loop can hand a claimed delivery to. Implementations are registered only for the
/// channels the deployment actually configures; a delivery whose channel has no registration is recorded as
/// permanently failed with a message saying exactly that (a queued message must never vanish silently).
/// </summary>
public interface INotificationChannel
{
    /// <summary>The channel name deliveries carry (a <see cref="Catalog.NotificationChannels"/> constant).</summary>
    string Name { get; }

    /// <summary>Sends one claimed delivery. Throws <see cref="NotificationSendException"/> with a retry verdict;
    /// any other exception is treated as retryable by the send loop.</summary>
    Task SendAsync(CatalogNotificationDelivery delivery, CancellationToken ct);
}

/// <summary>The email channel: hands the delivery's composed subject / text / HTML to the configured
/// <see cref="IEmailSender"/> (SMTP or Microsoft Graph, one of which is registered at startup).</summary>
public sealed class EmailNotificationChannel : INotificationChannel
{
    private readonly IEmailSender _sender;

    public EmailNotificationChannel(IEmailSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    public string Name => Catalog.NotificationChannels.Email;

    public Task SendAsync(CatalogNotificationDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return _sender.SendAsync(delivery.Target, delivery.Subject, delivery.TextBody, delivery.HtmlBody, ct);
    }
}

/// <summary>The Slack channel: posts the delivery's composed blocks (with the text body as the notification
/// fallback) to its target, resolving a <c>dm:{email}</c> target to the user's direct-message conversation first.</summary>
public sealed class SlackNotificationChannel : INotificationChannel
{
    private readonly ISlackApiClient _slack;

    public SlackNotificationChannel(ISlackApiClient slack)
    {
        ArgumentNullException.ThrowIfNull(slack);
        _slack = slack;
    }

    public string Name => Catalog.NotificationChannels.Slack;

    public async Task SendAsync(CatalogNotificationDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var channel = NotificationTargets.TryParseSlackDm(delivery.Target, out var email)
            ? await _slack.ResolveDirectChannelAsync(email, ct).ConfigureAwait(false)
            : delivery.Target;
        await _slack.PostMessageAsync(channel, delivery.TextBody, delivery.SlackBlocksJson, ct).ConfigureAwait(false);
    }
}
