using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.SourceControl.Proposals;

namespace SqlFlow.ControlPlane.Proposals;

/// <summary>
/// Opens a pull request on BitBucket Cloud via its 2.0 REST API
/// (<c>POST /2.0/repositories/{workspace}/{repo}/pullrequests</c>) using HTTP Basic auth with the repo source's
/// account username and app password (the same credential that pushed the proposal branch). BitBucket, unlike
/// GitHub, always needs the account username, so a source without <c>credentialUsername</c> is refused with a clear
/// message rather than a confusing 401. Any non-success response is mapped onto a <see cref="SqlFlowException"/>
/// with a secret-redacted, truncated body.
/// </summary>
public sealed class BitbucketPullRequestPublisher : IPullRequestPublisher
{
    private const string ApiBase = "https://api.bitbucket.org/2.0";

    private readonly IHttpClientFactory _httpClientFactory;

    public BitbucketPullRequestPublisher(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public PullRequestProvider Provider => PullRequestProvider.Bitbucket;

    public async Task<PullRequestResult> CreateAsync(
        PullRequestCoordinates coordinates, PullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            throw new SqlFlowException(
                "a BitBucket pull request requires the repo source's credentialUsername (the account name) alongside the app password.");
        }

        var payload = new JsonObject
        {
            ["title"] = request.Title,
            ["description"] = request.Body ?? string.Empty,
            ["source"] = new JsonObject { ["branch"] = new JsonObject { ["name"] = request.HeadBranch } },
            ["destination"] = new JsonObject { ["branch"] = new JsonObject { ["name"] = request.BaseBranch } },
        };

        var url = $"{ApiBase}/repositories/{Uri.EscapeDataString(coordinates.Owner)}/{Uri.EscapeDataString(coordinates.Repo)}/pullrequests";
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{request.Username.Trim()}:{request.Secret.Trim()}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.UserAgent.ParseAdd("sqlflow-control-plane");

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(GitHubPullRequestPublisher.HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlFlowException(
                $"the BitBucket pull-request request failed to complete: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SqlFlowException(
                    $"BitBucket pull-request creation returned HTTP {(int)response.StatusCode}: {SecretHygiene.RedactedMessage(Truncate(body))}");
            }

            return ParseResult(body);
        }
    }

    private static PullRequestResult ParseResult(string body)
    {
        JsonObject envelope;
        try
        {
            envelope = JsonNode.Parse(body) as JsonObject
                ?? throw new SqlFlowException("BitBucket returned a non-object pull-request response.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"BitBucket returned an unparseable pull-request response: {ex.Message}", ex);
        }

        var number = envelope["id"]?.GetValue<int>();
        var url = envelope["links"]?["html"]?["href"]?.GetValue<string>();
        if (number is null || string.IsNullOrWhiteSpace(url))
        {
            throw new SqlFlowException("BitBucket's pull-request response was missing the id or URL.");
        }

        return new PullRequestResult(url, number.Value);
    }

    private static string Truncate(string body) => body.Length > 500 ? body[..500] : body;
}
