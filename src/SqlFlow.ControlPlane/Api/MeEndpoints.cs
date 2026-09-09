using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Security;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The caller's own account surface, mapped under the <c>read</c> scope so any authenticated user manages their own
/// resources (never another user's). Today that is personal access tokens: long-lived bearer credentials for
/// headless clients (the CLI, the VSCode extension, automation) that cannot hold the browser's short session token.
/// A token is minted with scopes capped to the caller's own grant, shown once, and revocable by its owner; the
/// secret is never stored, only its hash.
/// </summary>
public static class MeEndpoints
{
    /// <summary>The longest a token name may be (matches the column bound); a label, not free-form content.</summary>
    private const int MaxNameLength = 200;

    /// <summary>The furthest out an expiry may be set. A decade is generous for a service credential while still
    /// bounding "never" to an explicit null rather than an accidental thousand-year date.</summary>
    private const int MaxExpiryDays = 3650;

    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/me", GetIdentity).WithTags("Access tokens").WithName("GetMyIdentity");
        group.MapGet("/me/tokens", ListTokensAsync).WithTags("Access tokens").WithName("ListMyAccessTokens");
        group.MapPost("/me/tokens", CreateTokenAsync).WithTags("Access tokens").WithName("CreateMyAccessToken");
        group.MapDelete("/me/tokens/{id:guid}", RevokeTokenAsync).WithTags("Access tokens").WithName("RevokeMyAccessToken");

        return group;
    }

    /// <summary>Who the presented bearer credential authenticates as: the subject, role, effective scopes, and
    /// whether a catalog user account backs it (a bootstrap token has none). The claims are already resolved by
    /// the authentication handler (for a PAT, scopes are the token's cap intersected with the owner's CURRENT
    /// role), so this is a pure read of the principal: the cheapest authoritative "whoami" a headless client
    /// (CLI, MCP, automation) can ask before doing real work.</summary>
    private static Ok<IdentityDto> GetIdentity(HttpContext httpContext)
    {
        var user = httpContext.User;
        var scopes = user.FindFirstValue("scope") is { Length: > 0 } scope
            ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return TypedResults.Ok(new IdentityDto(
            user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(unknown)",
            user.FindFirstValue("role"),
            scopes,
            TryGetUserId(user, out var userId) ? userId : null));
    }

    private static async Task<Results<Ok<IReadOnlyList<AccessTokenDto>>, ProblemHttpResult>> ListTokensAsync(
        CatalogDbContext catalog, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var tokens = await AccessTokenStore.ListForUserAsync(catalog, userId, ct).ConfigureAwait(false);
        IReadOnlyList<AccessTokenDto> dto = tokens.Select(ToDto).ToList();
        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Created<CreatedAccessTokenDto>, ProblemHttpResult>> CreateTokenAsync(
        CreateAccessTokenRequest request, CatalogDbContext catalog, TimeProvider clock, HttpContext httpContext,
        CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A token name is required");
        }

        var name = request.Name.Trim();
        if (name.Length > MaxNameLength)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Token name is too long", detail: $"A token name may be at most {MaxNameLength} characters.");
        }

        // The token can never grant more than the caller holds: cap the requested scopes to the caller's own, and
        // default an empty/omitted request to the caller's full set.
        var ownScopes = OwnScopes(httpContext.User);
        var requested = request.Scopes is { Count: > 0 } ? request.Scopes : ownScopes;
        var granted = requested
            .Where(s => ownScopes.Contains(s, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (granted.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "No grantable scope requested",
                detail: "A token's scopes must be a non-empty subset of your own scopes.");
        }

        if (request.ExpiresInDays is { } days && (days < 1 || days > MaxExpiryDays))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid expiry", detail: $"Expiry, when set, must be between 1 and {MaxExpiryDays} days.");
        }

        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var expiresUtc = request.ExpiresInDays is { } d ? nowUtc.AddDays(d) : (DateTime?)null;
        var minted = AccessTokenGenerator.Mint();
        var id = await AccessTokenStore.CreateAsync(
            catalog, userId, name, minted.Hash, minted.DisplayPrefix, string.Join(' ', granted), expiresUtc, nowUtc, ct)
            .ConfigureAwait(false);

        var dto = new AccessTokenDto(id, name, minted.DisplayPrefix, granted, nowUtc, expiresUtc, null, null);
        return TypedResults.Created($"/api/v1/me/tokens/{id}", new CreatedAccessTokenDto(dto, minted.Secret));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeTokenAsync(
        Guid id, CatalogDbContext catalog, TimeProvider clock, HttpContext httpContext, CancellationToken ct)
    {
        if (!TryGetUserId(httpContext.User, out var userId))
        {
            return NoUserAccount();
        }

        var revoked = await AccessTokenStore.RevokeAsync(catalog, userId, id, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return revoked
            ? TypedResults.NoContent()
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
                detail: "No active token with that id belongs to you.");
    }

    /// <summary>The caller's catalog id from the <c>uid</c> claim. A bootstrap-secret session has no user behind it
    /// (no <c>uid</c>), so it cannot own tokens; that is surfaced as a 400 by the callers.</summary>
    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
        => Guid.TryParse(user.FindFirst("uid")?.Value, out userId);

    private static string[] OwnScopes(ClaimsPrincipal user)
        => (user.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ProblemHttpResult NoUserAccount()
        => TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "No user account",
            detail: "Personal access tokens belong to a user account; this session is not backed by one.");

    private static AccessTokenDto ToDto(CatalogAccessToken token) => new(
        token.Id, token.Name, token.Prefix,
        token.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        token.CreatedUtc, token.ExpiresUtc, token.LastUsedUtc, token.RevokedUtc);
}
