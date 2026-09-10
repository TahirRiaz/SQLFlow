namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// One fully composed notification: everything a channel needs to send, with nothing left to render at send time.
/// <see cref="TextBody"/> is always present (the email text alternative and the Slack fallback);
/// <see cref="HtmlBody"/> is set for email, <see cref="SlackBlocksJson"/> (a Block Kit array) for Slack.
/// </summary>
public sealed record NotificationMessage(string Subject, string TextBody, string? HtmlBody, string? SlackBlocksJson);

/// <summary>
/// A channel send failure with an explicit retry verdict. <see cref="Retryable"/> failures (a network blip, a
/// rate limit, a 5xx) go back onto the outbox with backoff; non-retryable ones (an unknown Slack user, a rejected
/// recipient) are recorded as permanently failed at once, because retrying cannot fix a wrong destination. The
/// message must already be secret-redacted by the thrower (it is persisted on the delivery row).
/// </summary>
public sealed class NotificationSendException : Exception
{
    public NotificationSendException(string message, bool retryable, Exception? inner = null)
        : base(message, inner)
    {
        Retryable = retryable;
    }

    public bool Retryable { get; }
}

/// <summary>
/// The delivery <c>Target</c> string forms. Email deliveries carry the address verbatim. Slack deliveries carry
/// either a channel id (<c>C0123ABCD</c>) or <c>dm:{email}</c>, which the Slack channel resolves to the user's
/// direct-message conversation at send time (so a message composed before the user ever DM'd the bot still lands).
/// </summary>
public static class NotificationTargets
{
    public const string SlackDmPrefix = "dm:";

    public static string SlackDm(string email) => SlackDmPrefix + email;

    public static bool TryParseSlackDm(string target, out string email)
    {
        if (target.StartsWith(SlackDmPrefix, StringComparison.Ordinal) && target.Length > SlackDmPrefix.Length)
        {
            email = target[SlackDmPrefix.Length..];
            return true;
        }

        email = string.Empty;
        return false;
    }
}
