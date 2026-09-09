using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.SourceControl.Proposals;

namespace SqlFlow.ControlPlane.Proposals;

/// <summary>
/// Opens a pull request on GitHub via its REST API (<c>POST /repos/{owner}/{repo}/pulls</c>) using the repo source's
/// token as a bearer credential (the same token that pushed the proposal branch). Plain HTTP, like the Slack and
/// Graph clients; any non-success response is mapped onto a <see cref="SqlFlowException"/> with a secret-redacted,
/// truncated body so a 422 (for example "no commits between base and head") surfaces its reason without leaking.
/// </summary>
public sealed class GitHubPullRequestPublisher : IPullRequestPublisher
{
    /// <summary>The named HttpClient the pull-request publishers share (registered in Program).</summary>
    public const string HttpClientName = "sqlflow-git-pr";

    private const string ApiBase = "https://api.github.com";

    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubPullRequestPublisher(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public PullRequestProvider Provider => PullRequestProvider.GitHub;

    public async Task<PullRequestResult> CreateAsync(
        PullRequestCoordinates coordinates, PullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);

        var payload = new JsonObject
        {
            ["title"] = request.Title,
            ["head"] = request.HeadBranch,
            ["base"] = request.BaseBranch,
            ["body"] = request.Body ?? string.Empty,
        };

        var url = $"{ApiBase}/repos/{Uri.EscapeDataString(coordinates.Owner)}/{Uri.EscapeDataString(coordinates.Repo)}/pulls";
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Secret.Trim());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        message.Headers.UserAgent.ParseAdd("sqlflow-control-plane");
        message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlFlowException(
                $"the GitHub pull-request request failed to complete: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SqlFlowException(
                    $"GitHub pull-request creation returned HTTP {(int)response.StatusCode}: {SecretHygiene.RedactedMessage(Truncate(body))}");
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
                ?? throw new SqlFlowException("GitHub returned a non-object pull-request response.");
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"GitHub returned an unparseable pull-request response: {ex.Message}", ex);
        }

        var url = envelope["html_url"]?.GetValue<string>();
        var number = envelope["number"]?.GetValue<int>();
        if (string.IsNullOrWhiteSpace(url) || number is null)
        {
            throw new SqlFlowException("GitHub's pull-request response was missing the URL or number.");
        }

        return new PullRequestResult(url, number.Value);
    }

    private static string Truncate(string body) => body.Length > 500 ? body[..500] : body;
}
