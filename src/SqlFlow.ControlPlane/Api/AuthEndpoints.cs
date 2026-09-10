using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The sign-in surface. Three ways in, one token out:
/// <list type="bullet">
/// <item><c>POST /auth/login</c>: a regular SQLFlow user (username + password held in the catalog).</item>
/// <item><c>POST /auth/exchange</c>: Azure single sign-on. The SPA signs in against Microsoft Entra ID (MSAL,
/// auth code + PKCE) and exchanges the Entra ID token for a SQLFlow token; first sign-in provisions the user
/// just-in-time with the configured default role. Mapped only when Entra is enabled.</item>
/// <item><c>POST /auth/token</c>: the break-glass bootstrap secret, mapped only when one is configured.</item>
/// </list>
/// Every path issues the same HS256 SQLFlow token, so authorization downstream is identical regardless of how the
/// caller signed in. <c>GET /auth/providers</c> tells the login page which of these are available.
/// <para>Sessions roll rather than expire under the user: <c>POST /auth/renew</c> trades a live interactive token for
/// a fresh one, so a token stays short-lived (and a leaked one stays short-lived) while the person behind it stays
/// signed in for as long as they keep working. Renewal re-reads the account every time, so deactivating a user or
/// changing their role takes effect at their next roll instead of lingering for the life of an issued token.</para>
/// </summary>
public static class AuthEndpoints
{
    private static readonly string[] BootstrapAllowedScopes = ["read", "operate", "author", "admin"];

    /// <summary>A hash verified for sign-in attempts against unknown/ineligible accounts, so the response time
    /// does not reveal whether a username exists. Computed once from ephemeral random material.</summary>
    private static readonly Lazy<string> DecoyHash = new(() =>
        new PasswordHasher<CatalogUser>().HashPassword(new CatalogUser(), Guid.NewGuid().ToString("N")));

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder group, ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(options);

        group.MapGet("/auth/providers", GetProviders)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("GetAuthProviders");

        group.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("Login");

        // Roll a live interactive session onto a fresh token. Authenticated by the very token being replaced, so
        // there is no separate refresh credential to store, leak, or revoke.
        group.MapPost("/auth/renew", RenewAsync)
            .RequireAuthorization("read")
            .WithTags("Authentication")
            .WithName("RenewSession");

        // OAuth 2.0 device-authorization grant (RFC 8628): the sign-in path for the MCP server and any headless
        // client. Start and token polling are anonymous (the device_code is the secret); approval/denial run under
        // an authenticated browser session so a human binds their own identity and scopes to the device.
        group.MapPost("/auth/device", StartDeviceAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("StartDeviceAuthorization");

        group.MapPost("/auth/device/token", DeviceTokenAsync)
            .AllowAnonymous()
            .WithTags("Authentication")
            .WithName("PollDeviceToken");

        group.MapPost("/auth/device/approve", ApproveDeviceAsync)
            .RequireAuthorization("read")
            .WithTags("Authentication")
            .WithName("ApproveDeviceAuthorization");

        group.MapPost("/auth/device/deny", DenyDeviceAsync)
            .RequireAuthorization("read")
            .WithTags("Authentication")
            .WithName("DenyDeviceAuthorization");

        if (options.AzureAd.IsEnabled)
        {
            group.MapPost("/auth/exchange", ExchangeAsync)
                .AllowAnonymous()
                .WithTags("Authentication")
                .WithName("ExchangeExternalToken");
        }

        if (!string.IsNullOrEmpty(options.Jwt.BootstrapSecret))
        {
            group.MapPost("/auth/token", IssueBootstrapAsync)
                .AllowAnonymous()
                .WithTags("Authentication")
                .WithName("IssueBootstrapToken");
        }

        return group;
    }

    private static Ok<AuthProvidersDto> GetProviders(IOptions<ControlPlaneOptions> options)
    {
        var value = options.Value;
        var entra = value.AzureAd.IsEnabled
            ? new EntraProviderDto(true, value.AzureAd.ClientId, value.AzureAd.ResolveAuthority())
            : new EntraProviderDto(false, null, null);
        return TypedResults.Ok(new AuthProvidersDto(
            Local: true,
            Bootstrap: !string.IsNullOrEmpty(value.Jwt.BootstrapSecret),
            Entra: entra));
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, CatalogDbContext catalog, TokenIssuer issuer, IPasswordHasher<CatalogUser> hasher,
        LoginThrottle throttle, IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(AuthEndpoints));
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Username and password are required");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.IsLockedOut(request.Username, clientKey, nowUtc))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many failed sign-in attempts",
                detail: $"Sign-in for this account is paused for up to {LoginThrottle.LockoutSeconds / 60} minutes.");
        }

        var user = await UserStore.FindByUsernameAsync(catalog, request.Username, ct).ConfigureAwait(false);
        var eligible = user is { Active: true, Provider: UserProviders.Local, PasswordHash: not null };

        // Always verify exactly one hash, real or decoy, so "user exists" is not readable from the timing.
        var verified = hasher.VerifyHashedPassword(
            user ?? new CatalogUser(),
            eligible ? user!.PasswordHash! : DecoyHash.Value,
            request.Password);
        if (!eligible || verified == PasswordVerificationResult.Failed)
        {
            throttle.RecordFailure(request.Username, clientKey, nowUtc);
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password");
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The hasher's work factor increased since this hash was written; upgrade it now that the cleartext
            // is in hand. Best-effort: a failure leaves the old (still valid) hash in place.
            try
            {
                var upgraded = hasher.HashPassword(user!, request.Password);
                await UserStore.SetPasswordHashAsync(catalog, user!.Id, upgraded, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Password rehash for user {UserId} failed; the existing hash remains valid.", user!.Id);
            }
        }

        throttle.RecordSuccess(request.Username, clientKey);
        await UserStore.RecordLoginAsync(catalog, user!.Id, nowUtc, ct).ConfigureAwait(false);
        return await IssueSessionAsync(catalog, issuer, user, nowUtc, ct).ConfigureAwait(false);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> ExchangeAsync(
        ExchangeRequest request, CatalogDbContext catalog, TokenIssuer issuer, IExternalTokenValidator validator,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, HttpContext httpContext, CancellationToken ct)
    {
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "An identity token is required");
        }

        var profile = await validator.ValidateAsync(request.Token, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid identity token");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var (status, user) = await UserStore.EnsureExternalAsync(
            catalog, profile, options.Value.AzureAd.DefaultRole, nowUtc, ct).ConfigureAwait(false);
        return status switch
        {
            // The Entra token already proved who the caller is, so account state is safe to disclose.
            ExternalSignInStatus.Inactive => TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Account is deactivated",
                detail: "Your account exists but has been deactivated by an administrator."),
            ExternalSignInStatus.UsernameConflict => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Sign-in name already in use",
                detail: "A different account already uses this sign-in name; an administrator must resolve the conflict."),
            ExternalSignInStatus.DefaultRoleMissing => TypedResults.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Identity is not provisioned",
                detail: "The default role for single sign-on users does not exist; bootstrap provisioning has not completed."),
            _ => await IssueSessionAsync(catalog, issuer, user!, nowUtc, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Rolls a live interactive session onto a fresh token, keeping the original authentication time so the absolute
    /// cap is measured from the real sign-in. The presented token authenticates the call, which is what makes this
    /// safe without a second long-lived credential: a caller can only roll a session they already hold, and only
    /// while it is still valid. Once a session lapses there is no way back in but to sign in.
    /// <para>Refused for any credential that must not roll (no <c>auth_time</c>: a personal access token, the
    /// break-glass bootstrap token, a device grant), past the absolute cap, and for an account that has since been
    /// deactivated or had its role withdrawn.</para>
    /// </summary>
    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> RenewAsync(
        CatalogDbContext catalog, TokenIssuer issuer, IOptions<ControlPlaneOptions> options, TimeProvider clock,
        HttpContext httpContext, CancellationToken ct)
    {
        NeverCache(httpContext);
        var principal = httpContext.User;

        var authTimeClaim = principal.FindFirst(JwtRegisteredClaimNames.AuthTime)?.Value;
        if (!long.TryParse(authTimeClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authTimeUnix))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "This credential does not renew",
                detail: "Only an interactive sign-in session renews. A personal access token already carries its own lifetime, and a break-glass session is deliberately not extendable.");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var authTimeUtc = DateTimeOffset.FromUnixTimeSeconds(authTimeUnix).UtcDateTime;
        var maxAge = TimeSpan.FromDays(options.Value.Jwt.SessionMaxDays);
        if (nowUtc - authTimeUtc >= maxAge)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Session has reached its maximum age",
                detail: $"A session renews for at most {options.Value.Jwt.SessionMaxDays} days after signing in; sign in again to continue.");
        }

        if (!Guid.TryParse(principal.FindFirst("uid")?.Value, out var userId))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "This credential does not renew",
                detail: "The session is not backed by a user account.");
        }

        // Re-read the account on every roll. This is the point where a deactivation or a role change catches up with
        // an already-issued token, which is what keeps a long rolling session from outliving the authority behind it.
        var user = await UserStore.FindByIdAsync(catalog, userId, ct).ConfigureAwait(false);
        if (user is null || !user.Active)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Account is no longer active",
                detail: "This account has been deactivated or removed; sign in again if you believe this is an error.");
        }

        return await IssueSessionAsync(catalog, issuer, user, nowUtc, ct, authTimeUtc).ConfigureAwait(false);
    }

    /// <summary>Loads the user's role grants and issues the session. Fails closed (403) when the role row is
    /// gone: a user whose role was deleted has no defined scopes and must not get a fallback grant.
    /// <paramref name="authTimeUtc"/> carries the original sign-in time through a renewal; null means this
    /// <em>is</em> the sign-in, so the authentication time is now.</summary>
    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> IssueSessionAsync(
        CatalogDbContext catalog, TokenIssuer issuer, CatalogUser user, DateTime nowUtc, CancellationToken ct,
        DateTime? authTimeUtc = null)
    {
        var role = await UserStore.FindRoleAsync(catalog, user.Role, ct).ConfigureAwait(false);
        if (role is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Role is not provisioned",
                detail: $"The role '{user.Role}' assigned to this account does not exist; an administrator must reassign the account.");
        }

        var scopes = role.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = issuer.Issue(user.Username, scopes, nowUtc, user.Role, user.Id, authTimeUtc ?? nowUtc);
        var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - nowUtc).TotalSeconds);
        return TypedResults.Ok(new SessionResponse(result.Token, "Bearer", expiresIn, user.Username, user.Role, scopes));
    }

    private static Results<Ok<TokenResponse>, ProblemHttpResult> IssueBootstrapAsync(
        TokenRequest request, TokenIssuer issuer, IOptions<ControlPlaneOptions> options, TimeProvider clock,
        HttpContext httpContext)
    {
        NeverCache(httpContext);

        var configured = options.Value.Jwt.BootstrapSecret;
        if (string.IsNullOrEmpty(configured))
        {
            // The endpoint is not mapped when no bootstrap secret is set; this is defense in depth.
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (request is null || string.IsNullOrEmpty(request.Secret) || !FixedTimeEquals(request.Secret, configured))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid bootstrap secret");
        }

        var requested = request.Scopes is { Count: > 0 } ? request.Scopes : (IReadOnlyList<string>)["read"];
        var scopes = requested.Where(s => BootstrapAllowedScopes.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No valid scope requested",
                detail: $"Allowed scopes: {string.Join(", ", BootstrapAllowedScopes)}.");
        }

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? "bootstrap" : request.Subject.Trim();
        var result = issuer.Issue(subject, scopes, clock.GetUtcNow().UtcDateTime);
        var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - clock.GetUtcNow().UtcDateTime).TotalSeconds);
        return TypedResults.Ok(new TokenResponse(result.Token, "Bearer", expiresIn));
    }

    // --- Device authorization grant (RFC 8628) -------------------------------------------------------------------

    /// <summary>Scopes a device token may ever carry. Admin is intentionally excluded: a headless client signs in
    /// for read, operate, and authoring (proposing pipelines as pull requests), never account administration. The
    /// approval step still intersects these with what the approving user actually holds.</summary>
    private static readonly string[] DeviceAllowedScopes = ["read", "operate", "author"];

    private const int DeviceCodeTtlSeconds = 600;
    private const int DevicePollIntervalSeconds = 5;

    /// <summary>Begin a device flow: mint a device/user code pair and advertise the approval URL. Anonymous, since
    /// the opaque device_code is the only secret the polling client holds.</summary>
    private static Ok<DeviceAuthorizationResponse> StartDeviceAsync(
        DeviceAuthorizationRequest? request, DeviceCodeStore store, TimeProvider clock, HttpContext httpContext)
    {
        NeverCache(httpContext);

        var requested = ParseScopes(request?.Scope)
            .Where(s => DeviceAllowedScopes.Contains(s, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
        {
            requested = ["read"];
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var entry = store.Create(requested, nowUtc, DeviceCodeTtlSeconds, DevicePollIntervalSeconds);

        var origin = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var verificationUri = $"{origin}/device";
        var verificationUriComplete = $"{verificationUri}?code={Uri.EscapeDataString(entry.UserCode)}";
        var expiresIn = (int)Math.Max(1, (entry.ExpiresUtc - nowUtc).TotalSeconds);

        return TypedResults.Ok(new DeviceAuthorizationResponse(
            entry.DeviceCode, entry.UserCode, verificationUri, verificationUriComplete, expiresIn, entry.IntervalSeconds));
    }

    /// <summary>Poll for the device token. Returns the standard RFC 8628 error codes until the flow is approved,
    /// then mints a normal HS256 token for the approving user (scoped to the granted read/operate subset).</summary>
    private static Results<Ok<DeviceTokenResponse>, JsonHttpResult<DeviceErrorResponse>> DeviceTokenAsync(
        DeviceTokenRequest request, DeviceCodeStore store, TokenIssuer issuer, TimeProvider clock, HttpContext httpContext)
    {
        NeverCache(httpContext);
        var nowUtc = clock.GetUtcNow().UtcDateTime;

        if (request is null || string.IsNullOrWhiteSpace(request.DeviceCode))
        {
            return DeviceError("invalid_request");
        }

        var entry = store.FindByDeviceCode(request.DeviceCode, nowUtc);
        if (entry is null)
        {
            // Unknown or expired: RFC 8628 collapses both to expired_token from the client's perspective.
            return DeviceError("expired_token");
        }

        // Enforce the advertised minimum poll cadence.
        if (entry.LastPolledUtc is { } last && (nowUtc - last).TotalSeconds < entry.IntervalSeconds)
        {
            entry.LastPolledUtc = nowUtc;
            return DeviceError("slow_down");
        }

        entry.LastPolledUtc = nowUtc;

        switch (entry.Status)
        {
            case DeviceCodeStore.DeviceStatus.Pending:
                return DeviceError("authorization_pending");
            case DeviceCodeStore.DeviceStatus.Denied:
                store.Remove(entry);
                return DeviceError("access_denied");
            case DeviceCodeStore.DeviceStatus.Approved:
                var result = issuer.Issue(entry.Username!, entry.GrantedScopes!, nowUtc, entry.Role, entry.UserId);
                store.Remove(entry);
                var expiresIn = (int)Math.Max(1, (result.ExpiresUtc - nowUtc).TotalSeconds);
                return TypedResults.Ok(new DeviceTokenResponse(
                    result.Token, "Bearer", expiresIn, string.Join(' ', entry.GrantedScopes!)));
            default:
                return DeviceError("expired_token");
        }
    }

    /// <summary>Approve a device flow from an authenticated session, binding the caller's identity and the granted
    /// (requested ∩ own, minus admin) scopes to the pending entry.</summary>
    private static Results<NoContent, ProblemHttpResult> ApproveDeviceAsync(
        DeviceApprovalRequest request, DeviceCodeStore store, TimeProvider clock, HttpContext httpContext)
    {
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.UserCode))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A user code is required");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var entry = store.FindByUserCode(request.UserCode, nowUtc);
        if (entry is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown or expired code");
        }

        if (entry.Status != DeviceCodeStore.DeviceStatus.Pending)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "This code has already been resolved");
        }

        var user = httpContext.User;
        var username = user.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(username))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The session has no subject");
        }

        var ownScopes = (user.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var granted = entry.RequestedScopes
            .Where(s => ownScopes.Contains(s, StringComparer.Ordinal) && s != "admin")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (granted.Count == 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Insufficient scope",
                detail: "Your account does not hold any of the scopes this device requested.");
        }

        entry.Username = username;
        entry.GrantedScopes = granted;
        entry.Role = user.FindFirst("role")?.Value;
        entry.UserId = Guid.TryParse(user.FindFirst("uid")?.Value, out var uid) ? uid : null;
        entry.Status = DeviceCodeStore.DeviceStatus.Approved;
        return TypedResults.NoContent();
    }

    /// <summary>Deny a pending device flow from an authenticated session.</summary>
    private static Results<NoContent, ProblemHttpResult> DenyDeviceAsync(
        DeviceApprovalRequest request, DeviceCodeStore store, TimeProvider clock, HttpContext httpContext)
    {
        NeverCache(httpContext);
        if (request is null || string.IsNullOrWhiteSpace(request.UserCode))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A user code is required");
        }

        var entry = store.FindByUserCode(request.UserCode, clock.GetUtcNow().UtcDateTime);
        if (entry is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown or expired code");
        }

        entry.Status = DeviceCodeStore.DeviceStatus.Denied;
        return TypedResults.NoContent();
    }

    private static JsonHttpResult<DeviceErrorResponse> DeviceError(string error)
        => TypedResults.Json(new DeviceErrorResponse(error), statusCode: StatusCodes.Status400BadRequest);

    private static IEnumerable<string> ParseScopes(string? scope)
        => string.IsNullOrWhiteSpace(scope)
            ? DeviceAllowedScopes
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Sign-in responses carry bearer tokens; never let an intermediary cache them.</summary>
    private static void NeverCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.Pragma = "no-cache";
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Request body for <c>POST /auth/device</c>: an optional client id and space-delimited requested scopes
/// (capped server-side to read/operate).</summary>
public sealed record DeviceAuthorizationRequest(string? ClientId, string? Scope);

/// <summary>The device-authorization response (camelCase on the wire): the codes, the approval URL, and polling
/// hints.</summary>
public sealed record DeviceAuthorizationResponse(
    string DeviceCode, string UserCode, string VerificationUri, string VerificationUriComplete, int ExpiresIn, int Interval);

/// <summary>Request body for <c>POST /auth/device/token</c>.</summary>
public sealed record DeviceTokenRequest(string DeviceCode);

/// <summary>The minted device token. Field names follow the OAuth token-response convention (snake_case).</summary>
public sealed record DeviceTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

/// <summary>The RFC 8628 error envelope: <c>authorization_pending</c>, <c>slow_down</c>, <c>access_denied</c>,
/// <c>expired_token</c>, or <c>invalid_request</c>.</summary>
public sealed record DeviceErrorResponse([property: JsonPropertyName("error")] string Error);

/// <summary>Request body for <c>POST /auth/device/approve</c> and <c>/deny</c>.</summary>
public sealed record DeviceApprovalRequest(string UserCode);
