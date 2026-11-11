using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;

namespace SqlFlow.Dataverse;

/// <summary>
/// Sends the reader's GET requests to one Dataverse environment and returns the parsed JSON. Every request goes through
/// the shared <see cref="HttpExecutor"/> (rate limit, SSRF guard, retry with backoff that honours <c>Retry-After</c>, the
/// response size cap) and only ever to the environment's own host, so the bearer token never leaves it, whatever a
/// <c>@odata.nextLink</c> says. A refused token is renewed once; any other client error is reported with the service's
/// own message and what to do about the common ones.
/// </summary>
internal sealed class DataverseClient
{
    // Client errors the reader answers itself, so the message carries Dataverse's own error text in full. The statuses
    // the executor retries (408 timeout, 425 too early, 429 throttled) are left out so they keep being retried.
    private static readonly IReadOnlySet<int> AnsweredStatuses =
        Enumerable.Range(400, 100).Where(code => code is not (408 or 425 or 429)).ToHashSet();

    private readonly HttpExecutor _http;
    private readonly IDataverseTokenProvider _tokens;
    private readonly Uri _environment;

    public DataverseClient(HttpExecutor http, IDataverseTokenProvider tokens, Uri environment)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(environment);
        _http = http;
        _tokens = tokens;
        _environment = environment;
    }

    /// <summary>GETs <paramref name="url"/> and parses its JSON body.</summary>
    /// <param name="url">An absolute URL on the environment's host.</param>
    /// <param name="prefer">The <c>Prefer</c> header's value, or null.</param>
    /// <param name="what">What is being read, for messages (<c>the metadata of table 'account'</c>).</param>
    /// <param name="ct">Cancels the request and any wait between attempts.</param>
    /// <exception cref="SqlFlowException">The URL leaves the environment, the service refuses the request, or the body is not JSON.</exception>
    public async Task<JsonDocument> GetJsonAsync(Uri url, string? prefer, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        EnsureOnEnvironment(url, what);

        var token = await _tokens.GetTokenAsync(forceRefresh: false, ct).ConfigureAwait(false);
        var result = await SendAsync(url, prefer, token, what, ct).ConfigureAwait(false);
        if (result.Status == HttpStatusCode.Unauthorized)
        {
            // A token can be revoked or expire early; renew it once. A second refusal is about the identity itself.
            token = await _tokens.GetTokenAsync(forceRefresh: true, ct).ConfigureAwait(false);
            result = await SendAsync(url, prefer, token, what, ct).ConfigureAwait(false);
        }

        if ((int)result.Status >= 400)
        {
            throw Refused(result, what);
        }

        try
        {
            return JsonDocument.Parse(result.Body);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException(
                $"Reading {what} from the Dataverse environment {_environment}: the response is not valid JSON ({ex.Message}). "
                + $"It began: {Excerpt(result.Body)}", ex);
        }
    }

    private async Task<HttpFetchResult> SendAsync(Uri url, string? prefer, string token, string what, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(() => Request(url, prefer, token), AnsweredStatuses, responseCharset: null, ct).ConfigureAwait(false);
        }
        catch (HttpStatusException ex)
        {
            // A transient status the executor retried until its attempts ran out (429 throttling, a 5xx).
            throw new SqlFlowException(
                $"Reading {what} from the Dataverse environment {_environment} failed after every retry: {ex.Message}", ex);
        }
    }

    private static HttpRequestMessage Request(Uri url, string? prefer, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("OData-MaxVersion", "4.0");
        request.Headers.TryAddWithoutValidation("OData-Version", "4.0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(prefer))
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        return request;
    }

    private void EnsureOnEnvironment(Uri url, string what)
    {
        if (!url.IsAbsoluteUri
            || url.Scheme != Uri.UriSchemeHttps
            || !url.Host.Equals(_environment.Host, StringComparison.OrdinalIgnoreCase)
            || url.Port != _environment.Port)
        {
            throw new SqlFlowException(
                $"Reading {what}: the Dataverse environment {_environment} pointed the read at {url.GetLeftPart(UriPartial.Authority)}, "
                + "another host. The reader sends its token to the environment's own host only, so the read stops here.");
        }
    }

    private SqlFlowException Refused(HttpFetchResult result, string what)
    {
        var code = (int)result.Status;
        var service = ServiceMessage(result.Body);
        var tooLong = service.Contains("Uri string is too long", StringComparison.OrdinalIgnoreCase);
        var advice = code switch
        {
            400 when tooLong => " The request is too long for the service: shorten 'filter', or read fewer columns with 'columns' or 'excludeColumns'.",
            401 => " The identity was refused: it must be an application user in this environment (Power Platform admin center, "
                   + "Environment, Settings, Application users) and its token must be for this environment.",
            403 => " The identity signed in but may not read this: give its application user a security role with read access to the table.",
            404 => " Check the table's logical name and the environment URL.",
            414 or 431 => " The request is too long for the service: read fewer columns with 'columns' or 'excludeColumns'.",
            _ => string.Empty,
        };

        return new SqlFlowException(
            $"Reading {what} from the Dataverse environment {_environment} was refused: HTTP {code} {result.Status}: {service}.{advice}");
    }

    /// <summary>The service's own error text: <c>error.message</c> (with its code) from an OData error body, or an excerpt of whatever came back.</summary>
    private static string ServiceMessage(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString()!.TrimEnd('.');
                return error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(code.GetString())
                    ? $"{text} (code {code.GetString()})"
                    : text;
            }
        }
        catch (JsonException)
        {
            // Not an OData error body (a gateway's HTML page, say): the excerpt below says what came back instead.
        }

        return body.Length == 0 ? "(empty response)" : Excerpt(body);
    }

    private static string Excerpt(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 512));
        return body.Length > 512 ? text + "..." : text;
    }
}
