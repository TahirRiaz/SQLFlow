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
    /// is measured from the real sign-in rather than from the newest token. Leave it null for a credential that must
    /// not roll (the break-glass bootstrap token, and the device grant, whose long-lived path is a personal access
    /// token instead). <c>/auth/renew</c> refuses anything without this claim.</para></summary>
    public TokenResult Issue(
        string subject, IReadOnlyList<string> scopes, DateTime nowUtc, string? role = null, Guid? userId = null,
        DateTime? authTimeUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(scopes);

        var expires = nowUtc.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (authTimeUtc is { } authTime)
        {
            var seconds = new DateTimeOffset(DateTime.SpecifyKind(authTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
            claims.Add(new Claim(
                JwtRegisteredClaimNames.AuthTime,
                seconds.ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }

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

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = expires,
            SigningCredentials = _credentials,
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResult(token, expires);
    }
}

/// <summary>An issued token and its expiry.</summary>
public sealed record TokenResult(string Token, DateTime ExpiresUtc);
