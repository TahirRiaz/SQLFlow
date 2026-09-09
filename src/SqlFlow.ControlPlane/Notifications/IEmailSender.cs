namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// Sends one already-composed notification email. Two implementations exist behind this seam: an SMTP relay
/// (<see cref="SmtpEmailSender"/>) and Microsoft Graph <c>sendMail</c> (<see cref="GraphEmailSender"/>); which one
/// is registered is decided once at startup from <c>ControlPlane:Notifications:Email:Provider</c>, so the rest of
/// the pipeline is transport-agnostic. Implementations throw <see cref="NotificationSendException"/> with an
/// explicit retry verdict (and secret-redacted messages); any other exception is treated as retryable.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string textBody, string? htmlBody, CancellationToken ct);
}
