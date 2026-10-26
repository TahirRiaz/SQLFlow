using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The GitHub Actions token check against tokens signed the way GitHub signs them (RS256 under a key the issuer
/// publishes), with the issuer's metadata held in memory: a valid token's identity, and every way a token is refused, each
/// with a reason a workflow author can act on.
/// </summary>
public sealed class GitHubActionsTokenValidatorTests
{
    private const string Issuer = "https://token.actions.githubusercontent.com";
    private const string Audience = "https://sqlflow.example.com";

    private static readonly RSA Signer = RSA.Create(2048);
    private static readonly RSA OtherSigner = RSA.Create(2048);

    [Fact]
    public async Task A_valid_token_states_the_job()
    {
        var (validator, _) = Validator();
        var token = Sign(Claims());

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Problem);
        var identity = Assert.IsType<GitHubActionsIdentity>(result.Identity);
        Assert.Equal("acme/estate", identity.Repository);
        Assert.Equal("123456", identity.RepositoryId);
        Assert.Equal("refs/heads/main", identity.Ref);
        Assert.Equal(".github/workflows/deliver.yml", identity.WorkflowPath);
        Assert.Equal("production", identity.Environment);
        Assert.Equal("987654321", identity.RunId);
        Assert.Equal("2", identity.RunAttempt);
        Assert.Equal("https://github.com/acme/estate/actions/runs/987654321/attempts/2", identity.RunUrl("https://github.com/"));
        Assert.Equal(
            "GitHub acme/estate (id 123456), workflow .github/workflows/deliver.yml, refs/heads/main at 0123456, run 987654321 attempt 2, environment production, by octocat (push)",
            identity.Describe());
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_named_for_what_it_is()
    {
        var (validator, metadata) = Validator();
        var token = Sign(Claims(), issuer: "https://login.microsoftonline.com/tenant/v2.0");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.False(result.Outage);
        Assert.Contains("not by GitHub Actions", result.Problem, StringComparison.Ordinal);
        Assert.Equal(0, metadata.Reads);
    }

    [Fact]
    public async Task A_token_for_another_audience_says_which_audience_to_ask_for()
    {
        var (validator, _) = Validator();
        var token = Sign(Claims(), audience: "https://other-service.example.com");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Contains($"audience '{Audience}'", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_token_is_refused_as_expired()
    {
        var (validator, _) = Validator();
        var issued = DateTime.UtcNow.AddMinutes(-30);
        var token = Sign(Claims(), notBefore: issued, expires: issued.AddMinutes(5));

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Contains("expired", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_GitHub_never_published_is_refused_after_one_refresh()
    {
        var (validator, metadata) = Validator();
        var token = Sign(Claims(), signer: OtherSigner, keyId: "forged");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Contains("signature", result.Problem, StringComparison.Ordinal);
        Assert.Equal(1, metadata.Refreshes);
    }

    [Fact]
    public async Task A_key_GitHub_published_after_the_keys_were_read_is_found_by_the_refresh()
    {
        var (validator, metadata) = Validator();
        metadata.AfterRefresh = MetadataWith(Signer, "current", OtherSigner, "rolled");
        var token = Sign(Claims(), signer: OtherSigner, keyId: "rolled");

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Problem);
        Assert.NotNull(result.Identity);
        Assert.Equal(1, metadata.Refreshes);
    }

    [Fact]
    public async Task Unreadable_keys_are_an_outage_worth_retrying_not_a_bad_token()
    {
        var (validator, metadata) = Validator();
        metadata.Failure = new HttpRequestException("connection refused");

        var result = await validator.ValidateAsync(Sign(Claims()), CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.True(result.Outage);
        Assert.Contains("could not be read", result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_without_a_repository_or_ref_is_refused()
    {
        var (validator, _) = Validator();
        var claims = Claims().Where(c => c.Type != "ref").ToList();

        var result = await validator.ValidateAsync(Sign(claims), CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.Contains("no repository or ref", result.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public async Task Text_that_is_not_a_token_is_refused_without_reading_keys(string token)
    {
        var (validator, metadata) = Validator();

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
        Assert.NotNull(result.Problem);
        Assert.Equal(0, metadata.Reads);
    }

    [Fact]
    public async Task An_unsigned_token_is_refused()
    {
        var (validator, _) = Validator();
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(Claims()),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
        });

        var result = await validator.ValidateAsync(unsigned, CancellationToken.None);

        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task A_token_signed_with_HS256_under_a_published_key_is_refused()
    {
        // The algorithm is pinned to RS256, so a symmetric signature can never be passed off as GitHub's.
        var (validator, _) = Validator();
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "current" };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(Claims()),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });

        var result = await validator.ValidateAsync(token, CancellationToken.None);

        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData("acme/estate/.github/workflows/deliver.yml@refs/heads/main", ".github/workflows/deliver.yml")]
    [InlineData("ACME/Estate/.github/workflows/deliver.yml@refs/tags/v1", ".github/workflows/deliver.yml")]
    [InlineData("acme/estate/ci/flows.yaml@refs/pull/7/merge", "ci/flows.yaml")]
    [InlineData("other/repo/.github/workflows/x.yml@refs/heads/main", "other/repo/.github/workflows/x.yml")]
    public void The_workflow_path_is_read_from_the_workflow_ref(string workflowRef, string expected)
    {
        var identity = new GitHubActionsIdentity("acme/estate", null, null, null, "refs/heads/main", null, workflowRef, null, null, null, null, null, null, null);

        Assert.Equal(expected, identity.WorkflowPath);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("12x", "1", null)]
    [InlineData("42", null, "https://ghe.example.com/acme/estate/actions/runs/42")]
    [InlineData("42", "3", "https://ghe.example.com/acme/estate/actions/runs/42/attempts/3")]
    public void A_run_link_is_built_only_from_a_numeric_run(string? runId, string? attempt, string? expected)
    {
        var identity = new GitHubActionsIdentity("acme/estate", null, null, null, "refs/heads/main", null, null, null, null, null, runId, attempt, null, null);

        Assert.Equal(expected, identity.RunUrl("https://ghe.example.com"));
    }

    private static (GitHubActionsTokenValidator Validator, InMemoryMetadata Metadata) Validator()
    {
        var metadata = new InMemoryMetadata(MetadataWith(Signer, "current"));
        var options = new GitHubTriggerOptions { Issuer = Issuer, Audience = Audience };
        return (new GitHubActionsTokenValidator(options, metadata, NullLogger<GitHubActionsTokenValidator>.Instance), metadata);
    }

    private static OpenIdConnectConfiguration MetadataWith(RSA key, string keyId, RSA? second = null, string? secondId = null)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = keyId });
        if (second is not null)
        {
            configuration.SigningKeys.Add(new RsaSecurityKey(second.ExportParameters(false)) { KeyId = secondId });
        }

        return configuration;
    }

    internal static List<Claim> Claims() =>
    [
        new("sub", "repo:acme/estate:environment:production"),
        new("repository", "acme/estate"),
        new("repository_id", "123456"),
        new("repository_owner", "acme"),
        new("repository_owner_id", "42"),
        new("ref", "refs/heads/main"),
        new("sha", "0123456789abcdef0123456789abcdef01234567"),
        new("workflow_ref", "acme/estate/.github/workflows/deliver.yml@refs/heads/main"),
        new("job_workflow_ref", "acme/estate/.github/workflows/deliver.yml@refs/heads/main"),
        new("environment", "production"),
        new("event_name", "push"),
        new("run_id", "987654321"),
        new("run_attempt", "2"),
        new("actor", "octocat"),
    ];

    private static string Sign(
        IEnumerable<Claim> claims, string issuer = Issuer, string audience = Audience, RSA? signer = null, string keyId = "current",
        DateTime? notBefore = null, DateTime? expires = null)
    {
        var key = new RsaSecurityKey(signer ?? Signer) { KeyId = keyId };
        var from = notBefore ?? DateTime.UtcNow.AddMinutes(-1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = from,
            NotBefore = from,
            Expires = expires ?? from.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>The issuer's metadata held in memory: what a refresh brings, how often it was read and refreshed, and an
    /// outage on demand.</summary>
    private sealed class InMemoryMetadata(OpenIdConnectConfiguration current) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private OpenIdConnectConfiguration _current = current;

        public OpenIdConnectConfiguration? AfterRefresh { get; set; }

        public Exception? Failure { get; set; }

        public int Reads { get; private set; }

        public int Refreshes { get; private set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            Reads++;
            return Failure is not null ? Task.FromException<OpenIdConnectConfiguration>(Failure) : Task.FromResult(_current);
        }

        public void RequestRefresh()
        {
            Refreshes++;
            if (AfterRefresh is not null)
            {
                _current = AfterRefresh;
            }
        }
    }
}
