using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using SqlFlow.Azure;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Notifications;

/// <summary>
/// The Microsoft Graph transport for notification email: <c>POST /users/{sender}/sendMail</c> as the configured
/// mailbox, over plain HTTP (no Graph SDK; the payload is a dozen lines of JSON). Authentication is an explicit
/// app registration when one is configured, otherwise the ambient SqlFlow Azure credential (managed identity in
/// Azure), so the Microsoft 365 path needs no SMTP relay and no extra secret beyond what the platform provides.
/// The app needs the application permission <c>Mail.Send</c>, ideally bounded to the sending mailbox with an
/// application access policy.
/// </summary>
public sealed class GraphEmailSender : IEmailSender, IDisposable
{
    /// <summary>The named HttpClient this sender uses (registered in Program).</summary>
    public const string HttpClientName = "sqlflow-graph-mail";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretResolver _secrets;
    private readonly IAzureCredentialFactory _credentialFactory;
    private readonly NotificationOptions _options;

    private readonly SemaphoreSlim _credentialGate = new(1, 1);
    private volatile TokenCredential? _credential;

    public GraphEmailSender(
        IHttpClientFactory httpClientFactory,
        ISecretResolver secrets,
        IAzureCredentialFactory credentialFactory,
        IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _credentialFactory = credentialFactory;
        _options = options.Value.Notifications;
    }

    public async Task SendAsync(string toAddress, string subject, string textBody, string? htmlBody, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(textBody);

        var graph = _options.Email.Graph;
        var payload = BuildPayload(toAddress, subject, textBody, htmlBody);
        var url = $"{graph.BaseUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(graph.SenderId!)}/sendMail";

        AccessToken token;
        try
        {
            var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
            token = await credential.GetTokenAsync(new TokenRequestContext([graph.Scope]), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Token acquisition is environmental (identity endpoint, rotated secret). The cached credential is
            // dropped so the next attempt re-resolves the secret reference: a rotated client secret heals the
            // in-flight retries instead of being pinned by the cache.
            InvalidateCredential();
            throw new NotificationSendException(
                $"acquiring a Microsoft Graph token failed: {SecretHygiene.RedactedMessage(ex)}", retryable: true, ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NotificationSendException(
                $"the Microsoft Graph sendMail request failed to complete: {SecretHygiene.RedactedMessage(ex)}",
                retryable: true, ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return;
            }

            var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
            // 429 and 5xx are the transient class (throttling, service trouble); any other 4xx is a request that
            // will fail the same way every time (bad recipient, missing Mail.Send permission, unknown sender).
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            throw new NotificationSendException(
                $"Microsoft Graph sendMail returned {(int)response.StatusCode} {response.StatusCode}: {SecretHygiene.RedactedMessage(detail)}",
                retryable);
        }
    }

    /// <summary>The Graph sendMail body. The From header is deliberately omitted: the message goes out as the
    /// {sender} mailbox in the URL, and an explicit From would additionally require Send As rights. Internal so
    /// payload composition is testable without Graph.</summary>
    internal static JsonObject BuildPayload(string toAddress, string subject, string textBody, string? htmlBody)
        => new()
        {
            ["message"] = new JsonObject
            {
                ["subject"] = subject,
                ["body"] = new JsonObject
                {
                    ["contentType"] = htmlBody is null ? "Text" : "HTML",
                    ["content"] = htmlBody ?? textBody,
                },
                ["toRecipients"] = new JsonArray(
                    new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = toAddress } }),
            },
            ["saveToSentItems"] = false,
        };

    private async Task<TokenCredential> GetCredentialAsync(CancellationToken ct)
    {
        if (_credential is { } existing)
        {
            return existing;
        }

        await _credentialGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credential is null)
            {
                var graph = _options.Email.Graph;
                if (!string.IsNullOrWhiteSpace(graph.ClientId))
                {
                    var clientSecret = await _secrets.ResolveAsync(graph.ClientSecretReference!, ct).ConfigureAwait(false);
                    _credential = new ClientSecretCredential(graph.TenantId, graph.ClientId, clientSecret);
                }
                else
                {
                    _credential = _credentialFactory.Create();
                }
            }

            return _credential;
        }
        finally
        {
            _credentialGate.Release();
        }
    }

    /// <summary>Drops the cached credential (volatile reference write, atomic). A concurrent send may still use
    /// the stale instance once; its own failure lands back here, so the cache converges on the fresh secret.</summary>
    private void InvalidateCredential() => _credential = null;

    public void Dispose() => _credentialGate.Dispose();

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 1000 ? body[..1000] : body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"(the error body could not be read: {ex.Message})";
        }
    }
}
