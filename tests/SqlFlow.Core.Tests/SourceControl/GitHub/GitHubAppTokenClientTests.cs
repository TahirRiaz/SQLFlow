using System.Net;
using System.Text;
using System.Text.Json;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>
/// The installation-token client against a scripted GitHub: what it sends (a JSON web token the App's key signed, the
/// headers GitHub asks for, a body narrowing the token), how it reuses tokens and lets one caller mint while others
/// wait, how it finds a repository's installation, what each refusal from GitHub becomes, what it retries and how long
/// it waits, how it corrects for a host clock off GitHub's, which redirects it follows, and that no message, log text or
/// <c>ToString</c> ever carries a token, a signed token or the key.
/// </summary>
public sealed class GitHubAppTokenClientTests
{
    private const string TokenValue = "ghs_16C7e42F292c6912E7710c838347Ae178B4a";

    [Fact]
    public async Task GetToken_ForAnInstallation_PostsASignedRequestAndReturnsTheToken()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TokenValue, token.Token);
        Assert.Equal(42, token.InstallationId);
        Assert.Equal(h.Clock.Now.AddHours(1), token.ExpiresUtc);
        Assert.Equal("selected", token.RepositorySelection);
        Assert.Equal("read", token.Permissions["contents"]);

        var request = Assert.Single(h.GitHub.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/app/installations/42/access_tokens", request.Uri.ToString());
        Assert.Equal("{}", request.Body);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Contains("SQLFlow", request.Headers["User-Agent"], StringComparison.Ordinal);

        var jwt = request.Jwt;
        Assert.True(jwt.SignedByTestKey);
        Assert.Equal("RS256", jwt.Header.GetProperty("alg").GetString());
        Assert.Equal(GitHubAppHarness.AppId, jwt.Claims.GetProperty("iss").GetString());
        Assert.Equal(h.Clock.Now.ToUnixTimeSeconds() - 60, jwt.IssuedAt);
        Assert.Equal(h.Clock.Now.ToUnixTimeSeconds() + 540, jwt.ExpiresAt);
        Assert.True(jwt.ExpiresAt - jwt.IssuedAt <= 600, "GitHub accepts a JSON web token of at most ten minutes");
    }

    [Fact]
    public async Task GetToken_SignsANumericAppIdAsANumber()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        await h.Client.GetTokenAsync(h.Request("app=123456;key=testkey:app-key;installation=42"));

        var iss = Assert.Single(h.GitHub.Requests).Jwt.Claims.GetProperty("iss");
        Assert.Equal(JsonValueKind.Number, iss.ValueKind);
        Assert.Equal(123456, iss.GetInt64());
    }

    [Fact]
    public async Task GetToken_ForARepository_FindsItsInstallation_AndNarrowsTheTokenToItAndThePermissions()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) => Task.FromResult(r.Method == HttpMethod.Get ? h.GitHub.Installation(77) : h.GitHub.Token(TokenValue));

        var token = await h.Client.GetTokenAsync(h.Request("repository=acme/flows;permissions=contents:read"));

        Assert.Equal(77, token.InstallationId);
        Assert.Equal(new GitHubRepositoryName("acme", "flows"), token.Repository);
        var lookup = Assert.Single(h.GitHub.Gets);
        Assert.Equal("https://api.github.com/repos/acme/flows/installation", lookup.Uri.ToString());
        Assert.True(lookup.Jwt.SignedByTestKey);
        var mint = Assert.Single(h.GitHub.Posts);
        Assert.Equal("https://api.github.com/app/installations/77/access_tokens", mint.Uri.ToString());
        using var body = JsonDocument.Parse(mint.Body!);
        Assert.Equal("flows", Assert.Single(body.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
        Assert.Equal("read", body.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
    }

    [Fact]
    public async Task GetToken_AgainstGitHubEnterprise_UsesItsApiRoot()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) => Task.FromResult(r.Method == HttpMethod.Get ? h.GitHub.Installation(5) : h.GitHub.Token(TokenValue));

        await h.Client.GetTokenAsync(h.Request("repository=acme/flows;api=https://ghe.example.com/api/v3"));

        Assert.Equal("https://ghe.example.com/api/v3/repos/acme/flows/installation", h.GitHub.Gets.Single().Uri.ToString());
        Assert.Equal("https://ghe.example.com/api/v3/app/installations/5/access_tokens", h.GitHub.Posts.Single().Uri.ToString());
    }

    [Fact]
    public async Task GetToken_ReusesATokenForTheWindow_ThenMintsAnother()
    {
        using var h = new GitHubAppHarness();
        var minted = 0;
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token($"ghs_token{Interlocked.Increment(ref minted):D4}aaaaaaaaaaaaaaaa"));
        var request = h.Request("installation=42");

        var first = await h.Client.GetTokenAsync(request);
        h.Clock.Advance(TimeSpan.FromMinutes(9));
        var reused = await h.Client.GetTokenAsync(h.Request("installation=42"));
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        var renewed = await h.Client.GetTokenAsync(request);

        Assert.Same(first, reused);
        Assert.NotEqual(first.Token, renewed.Token);
        Assert.Equal(2, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task GetToken_NeverHandsOutAgainATokenWithLittleLifeLeft()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue, expiresAt: h.Clock.Now.AddMinutes(12)));

        await h.Client.GetTokenAsync(h.Request("installation=42"));
        await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(2, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task GetToken_KeepsTokensForDifferentPermissionsApart_ButFindsTheInstallationOnce()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, n, _) => Task.FromResult(r.Method == HttpMethod.Get ? h.GitHub.Installation(77) : h.GitHub.Token($"ghs_token{n:D4}aaaaaaaaaaaaaaaaaaaa"));

        var read = await h.Client.GetTokenAsync(h.Request("repository=acme/flows;permissions=contents:read"));
        var write = await h.Client.GetTokenAsync(h.Request("repository=acme/flows;permissions=contents:write"));

        Assert.NotEqual(read.Token, write.Token);
        Assert.Single(h.GitHub.Gets);
        Assert.Equal(2, h.GitHub.Posts.Count);
    }

    [Fact]
    public async Task GetToken_LetsOneCallerMint_WhileTheOthersWaitForItsToken()
    {
        using var h = new GitHubAppHarness();
        var release = new TaskCompletionSource();
        h.GitHub.Answer = async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.GitHub.Token(TokenValue);
        };

        var callers = Enumerable.Range(0, 24).Select(_ => h.Client.GetTokenAsync(h.Request("installation=42"))).ToList();
        await WaitUntil(() => h.GitHub.Requests.Count == 1);
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.Single(h.GitHub.Requests);
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
    }

    [Fact]
    public async Task GetToken_AWaiterThatGivesUp_DoesNotDisturbTheCallerMinting()
    {
        using var h = new GitHubAppHarness();
        var release = new TaskCompletionSource();
        h.GitHub.Answer = async (_, _, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.GitHub.Token(TokenValue);
        };

        var minting = h.Client.GetTokenAsync(h.Request("installation=42"));
        await WaitUntil(() => h.GitHub.Requests.Count == 1);
        using var giveUp = new CancellationTokenSource();
        var waiting = h.Client.GetTokenAsync(h.Request("installation=42"), giveUp.Token);
        await giveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        release.SetResult();
        var token = await minting;
        var later = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Same(token, later);
        Assert.Single(h.GitHub.Requests);
    }

    [Fact]
    public async Task GetToken_NeverCachesAFailure()
    {
        using var h = new GitHubAppHarness();
        var failing = true;
        h.GitHub.Answer = (_, _, _) => Task.FromResult(failing
            ? h.GitHub.Error(HttpStatusCode.Unauthorized, "A JSON web token could not be decoded")
            : h.GitHub.Token(TokenValue));

        await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));
        failing = false;
        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TokenValue, token.Token);
    }

    [Fact]
    public async Task GetToken_WhereTheAppIsNotInstalled_SaysSo_AndAsksAgainOnlyAfterAShortWhile()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));

        var first = await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));
        Assert.Equal("acme/flows", first.Repository);
        Assert.Contains($"GitHub App {GitHubAppHarness.AppId} is not installed on acme/flows", first.Message, StringComparison.Ordinal);
        Assert.Contains("Install the App on acme", first.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));
        Assert.Single(h.GitHub.Requests);

        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));
        Assert.Equal(2, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task GetToken_WhenARememberedInstallationIsGone_LooksItUpAgainOnce()
    {
        using var h = new GitHubAppHarness();
        var installation = 77;
        h.GitHub.Answer = (r, _, _) => Task.FromResult(
            r.Method == HttpMethod.Get
                ? h.GitHub.Installation(installation)
                : r.Uri.AbsolutePath.Contains("/77/", StringComparison.Ordinal) && installation == 78
                    ? h.GitHub.Error(HttpStatusCode.NotFound, "Not Found")
                    : h.GitHub.Token(TokenValue));

        await h.Client.GetTokenAsync(h.Request("repository=acme/flows;permissions=contents:read"));
        installation = 78;
        var token = await h.Client.GetTokenAsync(h.Request("repository=acme/flows;permissions=contents:write"));

        Assert.Equal(78, token.InstallationId);
        Assert.Equal(2, h.GitHub.Gets.Count);
    }

    [Fact]
    public async Task GetToken_ForAnUnknownInstallation_SaysWhereTheIdIsFound()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.IsNotType<GitHubAppNotInstalledException>(ex);
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("installation 42 of GitHub App", ex.Message, StringComparison.Ordinal);
        Assert.Contains("was not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GitHub request ABCD:1234", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_ForARepositoryOutsideTheInstallation_IsNotInstalled()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(
            HttpStatusCode.UnprocessableEntity, "There is at least one repository that does not exist or is not accessible to the parent installation."));

        var ex = await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => h.Client.GetTokenAsync(h.Request("installation=42;repository=acme/flows")));

        Assert.Contains("does not reach acme/flows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repository access", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_ForPermissionsTheAppLacks_NamesThemAndWhereToGrantThem()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(
            HttpStatusCode.UnprocessableEntity, "The permissions requested are not granted to this installation."));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42;permissions=contents:write,pull_requests:write")));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("is not granted contents:write,pull_requests:write", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Permissions & events", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_ForASuspendedInstallation_SaysWhoMustUnsuspendIt()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) => Task.FromResult(r.Method == HttpMethod.Get
            ? h.GitHub.Installation(77, "acme", suspendedAt: "2026-10-01T08:00:00Z")
            : h.GitHub.Token(TokenValue));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));
        Assert.Contains("installation 77", ex.Message, StringComparison.Ordinal);
        Assert.Contains("is suspended", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.GitHub.Posts);

        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.Forbidden, "This installation has been suspended"));
        var minted = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=9")));
        Assert.Contains("is suspended (HTTP 403", minted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_RefusedSignedToken_NamesTheAppTheKeyAndTheClock()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.Unauthorized, "A JSON web token could not be decoded"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains($"refused the signed token of GitHub App {GitHubAppHarness.AppId}", ex.Message, StringComparison.Ordinal);
        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("A JSON web token could not be decoded", ex.Message, StringComparison.Ordinal);
        Assert.Contains("clock", ex.Message, StringComparison.Ordinal);
        Assert.Single(h.GitHub.Requests);
    }

    [Fact]
    public async Task GetToken_OnAClockOffGitHubs_SignsAgainOnGitHubsTime_AndTimesTheTokenOnItsOwn()
    {
        using var h = new GitHubAppHarness();
        var github = h.Clock.Now.AddMinutes(25);
        h.GitHub.Answer = (r, n, _) => Task.FromResult(n == 1
            ? h.GitHub.Error(HttpStatusCode.Unauthorized, "'Issued at' claim ('iat') must be an Integer representing a time in the past", github)
            : h.GitHub.Token(TokenValue, date: github));

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        var requests = h.GitHub.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(h.Clock.Now.ToUnixTimeSeconds() - 60, requests[0].Jwt.IssuedAt);
        Assert.Equal(github.ToUnixTimeSeconds() - 60, requests[1].Jwt.IssuedAt);

        // GitHub's expiry is an hour after its own clock: on this host's clock, an hour from now.
        Assert.Equal(h.Clock.Now.AddHours(1), token.ExpiresUtc);

        // The difference is remembered: the next token is signed on GitHub's time at once.
        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue, date: h.Clock.Now.AddMinutes(25)));
        await h.Client.GetTokenAsync(h.Request("installation=42"));
        Assert.Equal(h.Clock.Now.AddMinutes(25).ToUnixTimeSeconds() - 60, h.GitHub.Requests.Last().Jwt.IssuedAt);
    }

    [Fact]
    public async Task GetToken_RetriesWhatGitHubMayAnswerDifferentlyAMomentLater()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, n, _) => Task.FromResult(n switch
        {
            1 => h.GitHub.Error(HttpStatusCode.BadGateway, "Server Error"),
            2 => h.GitHub.Error(HttpStatusCode.ServiceUnavailable, "Unavailable"),
            _ => h.GitHub.Token(TokenValue),
        });

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TokenValue, token.Token);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, h.Delays);
    }

    [Fact]
    public async Task GetToken_GivesUpAfterItsAttempts_SayingWhatGitHubLastAnswered()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.InternalServerError, "Server Error"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Equal(3, h.GitHub.Requests.Count);
        Assert.Contains("after 3 attempts", ex.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_RetriesANetworkFailure()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, n, _) => n == 1
            ? throw new HttpRequestException("No such host is known. (api.github.com:443)")
            : Task.FromResult(h.GitHub.Token(TokenValue));

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TokenValue, token.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(h.Delays));
    }

    [Fact]
    public async Task GetToken_AbandonsAnAttemptGitHubDoesNotAnswer_AndSaysSoWhenNoneAnswers()
    {
        using var h = new GitHubAppHarness(options: new GitHubAppClientOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(100),
            Delay = (_, _) => Task.CompletedTask,
        });
        h.GitHub.Answer = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Equal(3, h.GitHub.Requests.Count);
        Assert.Contains("could not reach GitHub", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no answer within", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_StopsAtOnce_WhenTheCallerGivesUp()
    {
        using var h = new GitHubAppHarness();
        using var cts = new CancellationTokenSource();
        h.GitHub.Answer = async (_, _, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Client.GetTokenAsync(h.Request("installation=42"), cts.Token));

        Assert.Single(h.GitHub.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task GetToken_WaitsAsLongAsARateLimitAsks()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, n, _) =>
        {
            if (n > 1)
            {
                return Task.FromResult(h.GitHub.Token(TokenValue));
            }

            var limited = h.GitHub.Error(HttpStatusCode.TooManyRequests, "API rate limit exceeded");
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return Task.FromResult(limited);
        };

        await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(h.Delays));
    }

    [Fact]
    public async Task GetToken_WaitsUntilAnExhaustedRateLimitResets_OnGitHubsClock()
    {
        using var h = new GitHubAppHarness();
        var github = h.Clock.Now.AddMinutes(3);
        h.GitHub.Answer = (_, n, _) =>
        {
            if (n > 1)
            {
                return Task.FromResult(h.GitHub.Token(TokenValue));
            }

            var limited = h.GitHub.Error(HttpStatusCode.Forbidden, "API rate limit exceeded for installation", github);
            limited.Headers.Add("x-ratelimit-remaining", "0");
            limited.Headers.Add("x-ratelimit-reset", github.AddSeconds(12).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(limited);
        };

        await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(TimeSpan.FromSeconds(12), Assert.Single(h.Delays));
    }

    [Fact]
    public async Task GetToken_DoesNotWaitOutALongRateLimit_ButSaysHowLongItIs()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) =>
        {
            var limited = h.GitHub.Error(HttpStatusCode.Forbidden, "You have exceeded a secondary rate limit");
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(limited);
        };

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Contains("rate limiting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("120 seconds", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Delays);
        Assert.Single(h.GitHub.Requests);
    }

    [Fact]
    public async Task GetToken_TreatsASecondaryRateLimitNamingNoWait_AsAMinutesWait()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.Forbidden, "You have exceeded a secondary rate limit."));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Contains("60 seconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_FollowsARedirectWithinTheApi_CarryingTheSignedToken()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) =>
        {
            if (r.Uri.AbsolutePath == "/repos/acme/flows/installation")
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri("/repositories/1296269/installation", UriKind.Relative);
                return Task.FromResult(moved);
            }

            return Task.FromResult(r.Method == HttpMethod.Get ? h.GitHub.Installation(77) : h.GitHub.Token(TokenValue));
        };

        var token = await h.Client.GetTokenAsync(h.Request("repository=acme/flows"));

        Assert.Equal(77, token.InstallationId);
        var followed = h.GitHub.Gets[^1];
        Assert.Equal("https://api.github.com/repositories/1296269/installation", followed.Uri.ToString());
        Assert.True(followed.Jwt.SignedByTestKey);
    }

    [Fact]
    public async Task GetToken_RefusesARedirectAwayFromTheApi_SendingNothingThere()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://collector.example.net/steal");
            return Task.FromResult(moved);
        };

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));

        Assert.Contains("https://collector.example.net", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not sent there", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.GitHub.Requests, r => r.Uri.Host == "collector.example.net");
    }

    [Fact]
    public async Task GetToken_DoesNotRepeatATokenRequestAsAnotherMethod()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.Found);
            moved.Headers.Location = new Uri("https://api.github.com/elsewhere");
            return Task.FromResult(moved);
        };

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Contains("HTTP 302", ex.Message, StringComparison.Ordinal);
        Assert.Single(h.GitHub.Requests);
    }

    [Fact]
    public async Task GetToken_StopsFollowingAnEndlessRedirect()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) =>
        {
            var moved = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            moved.Headers.Location = new Uri(r.Uri, "/loop" + r.Uri.AbsolutePath.Length);
            return Task.FromResult(moved);
        };

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("repository=acme/flows")));

        Assert.Contains("more than 3 times", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_RefusesAnOversizedAnswer()
    {
        using var h = new GitHubAppHarness(options: new GitHubAppClientOptions { MaxBodyBytes = 64, Delay = (_, _) => Task.CompletedTask });
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Json(HttpStatusCode.Created, "{\"token\":\"" + new string('a', 200) + "\"}"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"token\":\"ghs_abc\" not json", "not the token response")]
    [InlineData("{\"expires_at\":\"2026-10-22T13:00:00Z\"}", "without a usable token")]
    [InlineData("{\"token\":\"ghs_16C7e42F292c6912E7710c838347Ae178B4a\"}", "without a usable token")]
    [InlineData("{\"token\":\"ghs_16C7e42F 292c6912\",\"expires_at\":\"2026-10-22T13:00:00Z\"}", "without a usable token")]
    [InlineData("[]", "without a usable token")]
    public async Task GetToken_RefusesAnAnswerThatIsNotAUsableToken_WithoutQuotingIt(string body, string expected)
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Json(HttpStatusCode.Created, body));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.IsAssignableFrom<GitHubAppException>(ex);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghs_", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_RefusesATokenThatHasAlreadyExpired()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue, expiresAt: h.Clock.Now.AddMinutes(-1)));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.Contains("already expired", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenValue, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetToken_TakesTheStatelessTokenFormat_WhateverItsLength()
    {
        using var h = new GitHubAppHarness();
        var stateless = "ghs_123456_eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiIxMjM0NTYifQ." + new string('x', 400);
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(stateless));

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.Equal(stateless, token.Token);
    }

    [Fact]
    public async Task GetToken_SaysWhichKeyCouldNotBeRead()
    {
        using var h = new GitHubAppHarness();

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("app=1;key=testkey:absent;installation=42")));

        Assert.Contains("could not read the private key of GitHub App 1 from ${testkey:absent}", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.GitHub.Requests);
    }

    [Fact]
    public async Task GetToken_RefusesARequestNamingNeitherInstallationNorRepository()
    {
        using var h = new GitHubAppHarness();
        var request = GitHubAppReference.Parse("app=1;key=testkey:app-key", h.Settings, requireTarget: false);

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(request));

        Assert.Contains("names neither", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.GitHub.Requests);
    }

    [Fact]
    public async Task Failures_NeverCarryATokenOrASignedToken_EvenWhenGitHubsMessageQuotesOne()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) => Task.FromResult(h.GitHub.Error(
            HttpStatusCode.BadRequest, $"Bad credentials: {TokenValue} and {r.Authorization}"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetTokenAsync(h.Request("installation=42")));

        Assert.DoesNotContain(TokenValue, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 900, "GitHub's message is shortened");
    }

    [Fact]
    public async Task Token_ToString_NeverShowsTheToken()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        var token = await h.Client.GetTokenAsync(h.Request("installation=42"));

        Assert.DoesNotContain(TokenValue, token.ToString(), StringComparison.Ordinal);
        Assert.Contains("installation 42", token.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetApp_ReadsTheAppAndItsInstallations_AsTheApp()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (r, _, _) =>
        {
            if (r.Uri.AbsolutePath == "/app")
            {
                return Task.FromResult(h.GitHub.Json(HttpStatusCode.OK,
                    """{"name":"SQLFlow sync","slug":"sqlflow-sync","owner":{"login":"acme"},"html_url":"https://github.com/apps/sqlflow-sync"}"""));
            }

            var listed = h.GitHub.Json(HttpStatusCode.OK,
                """[{"id":77,"account":{"login":"acme","type":"Organization"},"repository_selection":"selected","suspended_at":null},{"id":78,"account":{"login":"someone","type":"User"},"repository_selection":"all","suspended_at":"2026-09-01T00:00:00Z"},{"account":{"login":"no-id"}}]""");
            listed.Headers.Add("Link", "<https://api.github.com/app/installations?page=2>; rel=\"next\"");
            return Task.FromResult(listed);
        };

        var details = await h.Client.GetAppAsync(h.Settings.App!, h.Settings.ApiRoot);

        Assert.Equal("SQLFlow sync", details.Name);
        Assert.Equal("sqlflow-sync", details.Slug);
        Assert.Equal("acme", details.Owner);
        Assert.Equal(2, details.Installations.Count);
        Assert.Equal(new GitHubAppInstallation(77, "acme", "Organization", "selected", null), details.Installations[0]);
        Assert.NotNull(details.Installations[1].SuspendedUtc);
        Assert.True(details.MoreInstallations);
        Assert.Equal("https://api.github.com/app/installations?per_page=100", h.GitHub.Requests.Last().Uri.ToString());
        Assert.All(h.GitHub.Requests, r => Assert.True(r.Jwt.SignedByTestKey));
    }

    [Fact]
    public async Task GetApp_WithAKeyThatIsNotTheApps_SaysSo()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.Unauthorized, "A JSON web token could not be decoded"));

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Client.GetAppAsync(h.Settings.App!, h.Settings.ApiRoot));

        Assert.Contains("one of the App's current keys", ex.Message, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition was not met in time");
    }
}
