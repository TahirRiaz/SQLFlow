using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Security;

/// <summary>Scheme names for the bearer authentication pipeline: a policy scheme that inspects the token and forwards
/// to either JWT validation or the personal-access-token handler.</summary>
public static class PersonalAccessTokenDefaults
{
    /// <summary>The policy scheme every request authenticates against; it routes by token shape to either JWT
    /// validation or the PAT handler. Deliberately not "Bearer": that name is <c>JwtBearerDefaults</c>'s own
    /// scheme, which this forwards to.</summary>
    public const string PolicyScheme = "SmartBearer";

    /// <summary>The concrete scheme that validates a SQLFlow personal access token.</summary>
    public const string Scheme = "PersonalAccessToken";
}

/// <summary>
/// Authenticates a SQLFlow personal access token presented as an HTTP bearer credential. The token is hashed and
/// looked up in the catalog; a match that is active, unexpired, owned by an active user, and backed by a provisioned
/// role authenticates as that user. The effective scopes are the token's own cap intersected with the owner's
/// <em>current</em> role scopes, so demoting or deactivating the owner immediately narrows or disables every token
/// they hold with no change to the token rows. The resulting principal is claim-for-claim identical to a JWT-issued
/// one (<c>sub</c>/<c>scope</c>/<c>role</c>/<c>uid</c>), so every authorization policy downstream behaves the same
/// regardless of how the caller authenticated.
/// </summary>
public sealed class PersonalAccessTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Last-used is refreshed at most this often per token, so a token driving a busy client does not incur
    /// a database write on every request just to move a telemetry timestamp forward.</summary>
    private static readonly TimeSpan LastUsedRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly CatalogDbContext _catalog;
    private readonly TimeProvider _clock;

    public PersonalAccessTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        CatalogDbContext catalog, TimeProvider clock)
        : base(options, logger, encoder)
    {
        _catalog = catalog;
        _clock = clock;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryReadBearer(out var presented) || !AccessTokenGenerator.LooksLikeToken(presented))
        {
            // No SQLFlow PAT on this request: not our credential to judge.
            return AuthenticateResult.NoResult();
        }

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var hash = AccessTokenGenerator.HashSecret(presented);
        var auth = await AccessTokenStore.FindActiveByHashAsync(_catalog, hash, nowUtc, Context.RequestAborted)
            .ConfigureAwait(false);
        if (auth is null)
        {
            // Unknown, revoked, expired, or the owner is inactive / has no role. All collapse to one answer so a
            // caller cannot distinguish why their token stopped working.
            return AuthenticateResult.Fail("The access token is not valid.");
        }

        var effectiveScopes = auth.TokenScopes
            .Where(s => auth.RoleScopes.Contains(s, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, auth.Username),
            new("role", auth.Role),
            new("uid", auth.UserId.ToString("D")),
        };
        if (effectiveScopes.Length > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', effectiveScopes)));
        }

        await RefreshLastUsedAsync(auth, nowUtc).ConfigureAwait(false);

        var identity = new ClaimsIdentity(claims, PersonalAccessTokenDefaults.Scheme);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, PersonalAccessTokenDefaults.Scheme));
    }

    /// <summary>Advances the token's last-used timestamp, but only when the stored value is stale, and never lets a
    /// failure of this telemetry write fail the authentication it decorates.</summary>
    private async Task RefreshLastUsedAsync(AccessTokenAuth auth, DateTime nowUtc)
    {
        if (auth.LastUsedUtc is { } last && nowUtc - last < LastUsedRefreshInterval)
        {
            return;
        }

        try
        {
            await AccessTokenStore.TouchLastUsedAsync(_catalog, auth.TokenId, nowUtc, Context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Updating last-used time for access token {TokenId} failed; authentication stands.", auth.TokenId);
        }
    }

    private bool TryReadBearer(out string token)
    {
        token = string.Empty;
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = header[prefix.Length..].Trim();
        return token.Length > 0;
    }
}
