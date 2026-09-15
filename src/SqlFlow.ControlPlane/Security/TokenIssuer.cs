using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Issues HS256 bearer tokens signed with the configured key, valid against the same issuer/audience the host
/// validates. The foundation uses this for the guarded bootstrap endpoint; the Identity phase swaps the
/// implementation for an asymmetric key / external provider without changing the validation contract or callers.
/// </summary>
public sealed class TokenIssuer
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;

    public TokenIssuer(IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Jwt;
        // Validated at startup to be present and >= 32 bytes; constructing the key here is safe.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey!));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Issues a token for the subject with the given scopes (space-delimited <c>scope</c> claim), valid
    /// for the configured access-token lifetime. A user-backed token also carries the user's role and catalog id
    /// (<c>role</c> / <c>uid</c> claims) so the GUI can shape itself without a second call; a bootstrap token
    /// carries neither. <paramref name="nowUtc"/> is injectable for deterministic tests.
    /// <para><paramref name="authTimeUtc"/> marks the token as an interactive session that may roll: it records when
    /// the user actually proved who they are, and survives unchanged across every renewal so the absolute session cap
    /// is measured from the real sign-in rather than from the newest token. A device grant inherits the approving
    /// session's value, so approving a device never extends the approver's own cap. Leave it null for a credential
    /// that must not roll (the break-glass bootstrap token, or a device approved by a credential that does not roll
    /// itself). <c>/auth/renew</c> refuses anything without this claim.</para></summary>
    public TokenResult Issue(
        string subject, IReadOnlyList<string> scopes, DateTime nowUtc, string? role = null, Guid? userId = null,
        DateTime? authTimeUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(scopes);

        var claims = SubjectClaims(subject, scopes, role, userId);
        if (authTimeUtc is { } authTime)
        {
            var seconds = new DateTimeOffset(DateTime.SpecifyKind(authTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
            claims.Add(new Claim(
                JwtRegisteredClaimNames.AuthTime,
                seconds.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }

        return Sign(claims, nowUtc, nowUtc.AddMinutes(_options.AccessTokenMinutes));
    }

    /// <summary>
    /// Issues the token one AI assistant run presents to the MCP server (see <see cref="AssistantDelegation"/>): the
    /// same subject, role and catalog id as <paramref name="caller"/>, the caller's scopes minus administration and
    /// fleet traffic, the <c>token_use=assistant</c> marker the control plane fences, no <c>auth_time</c> so it can
    /// never be renewed, and a lifetime of <paramref name="lifetime"/> rather than the session lifetime.
    /// </summary>
    public TokenResult IssueAssistantDelegation(ClaimsPrincipal caller, TimeSpan lifetime, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var subject = caller.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(subject, nameof(caller));
        Guid? userId = Guid.TryParse(caller.FindFirst("uid")?.Value, out var uid) ? uid : null;

        var claims = SubjectClaims(
            subject, AssistantDelegation.DelegableScopes(caller), caller.FindFirst("role")?.Value, userId);
        claims.Add(new Claim(AssistantDelegation.ClaimType, AssistantDelegation.ClaimValue));
        return Sign(claims, nowUtc, nowUtc.Add(lifetime));
    }

    private static List<Claim> SubjectClaims(string subject, IReadOnlyList<string> scopes, string? role, Guid? userId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        if (scopes.Count > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', scopes)));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim("role", role));
        }

        if (userId is not null)
        {
            claims.Add(new Claim("uid", userId.Value.ToString("D")));
        }

        return claims;
    }

    private TokenResult Sign(List<Claim> claims, DateTime nowUtc, DateTime expiresUtc)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expiresUtc,
            SigningCredentials = _credentials,
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResult(token, expiresUtc);
    }
}

/// <summary>An issued token and its expiry.</summary>
public sealed record TokenResult(string Token, DateTime ExpiresUtc);
