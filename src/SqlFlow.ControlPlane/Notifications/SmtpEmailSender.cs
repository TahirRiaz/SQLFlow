using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// The SMTP transport for notification email, on MailKit (fully async, implicit-TLS and STARTTLS capable; the
/// legacy System.Net.Mail client is explicitly not recommended for new code). Each send is its own short
/// connect / authenticate / send / disconnect conversation: notification volume is one coalesced message per
/// subscriber per window, so connection reuse would buy nothing and a pooled connection could not survive relay
/// idle timeouts anyway. Credentials are secret references resolved per send, so a rotated password heals
/// in-flight retries without a restart.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ISecretResolver _secrets;
    private readonly NotificationOptions _options;

    public SmtpEmailSender(ISecretResolver secrets, IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(options);
        _secrets = secrets;
        _options = options.Value.Notifications;
    }

    public async Task SendAsync(string toAddress, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(textBody);

        var email = _options.Email;
        var smtp = email.Smtp;
        var message = BuildMessage(email.FromAddress!, email.FromDisplayName, toAddress, subject, textBody, htmlBody);

        using var client = new SmtpClient();
        client.Timeout = smtp.TimeoutSeconds * 1000;
        try
        {
            // Host is validated non-null at startup (Validate() refuses an smtp provider without one).
            await client.ConnectAsync(smtp.Host!, smtp.Port, MapSslMode(smtp.SslMode), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(smtp.UsernameReference))
            {
                var username = await _secrets.ResolveAsync(smtp.UsernameReference, ct).ConfigureAwait(false);
                var password = await _secrets.ResolveAsync(smtp.PasswordReference!, ct).ConfigureAwait(false);
                await client.AuthenticateAsync(username, password, ct).ConfigureAwait(false);
            }

            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
        }
        catch (SmtpCommandException ex)
        {
            // SMTP semantics: 5xx is a permanent rejection (unknown recipient, policy), 4xx a transient one
            // (mailbox busy, greylisting). Only the permanent class is not worth retrying.
            var permanent = (int)ex.StatusCode >= 500;
            throw new NotificationSendException(
                $"SMTP {smtp.Host}:{smtp.Port} rejected the message ({(int)ex.StatusCode} {ex.ErrorCode}): {SecretHygiene.RedactedMessage(ex)}",
                retryable: !permanent, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NotificationSendException)
        {
            // Connect/TLS/auth/IO failures: all environmental, all worth the backoff retry. Authentication
            // failures included: credentials are re-resolved per attempt, so a fixed secret heals the retries.
            throw new NotificationSendException(
                $"SMTP send via {smtp.Host}:{smtp.Port} failed: {SecretHygiene.RedactedMessage(ex)}",
                retryable: true, ex);
        }
    }

    /// <summary>Builds the MIME message: multipart/alternative with the plain-text part always present, so a
    /// text-only client still gets the full content. Internal so composition is testable without a relay.</summary>
    internal static MimeMessage BuildMessage(
        string fromAddress, string fromDisplayName, string toAddress, string subject, string textBody, string? htmlBody)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromDisplayName, fromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        message.Subject = subject;
        var body = new BodyBuilder { TextBody = textBody };
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            body.HtmlBody = htmlBody;
        }

        message.Body = body.ToMessageBody();
        return message;
    }

    internal static SecureSocketOptions MapSslMode(string sslMode) => sslMode.Trim().ToLowerInvariant() switch
    {
        "none" => SecureSocketOptions.None,
        "ssl" => SecureSocketOptions.SslOnConnect,
        "auto" => SecureSocketOptions.Auto,
        // "starttls" (the validated default): TLS is required, not opportunistic.
        _ => SecureSocketOptions.StartTls,
    };
}
