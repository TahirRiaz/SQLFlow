using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>The identity and grant a valid personal access token resolves to at authentication time: the owning
/// user, the token's own scope cap, and the owner's current role scopes. The effective grant is
/// <see cref="TokenScopes"/> intersected with <see cref="RoleScopes"/>, computed by the caller so a role change
/// takes effect on the next request without rewriting the token row.</summary>
public sealed record AccessTokenAuth(
    Guid TokenId, Guid UserId, string Username, string Role,
    IReadOnlyList<string> TokenScopes, IReadOnlyList<string> RoleScopes, DateTime? LastUsedUtc);

/// <summary>
/// Persistence for personal access tokens. Stateless like the other catalog stores (see <see cref="UserStore"/>):
/// secret generation and hashing are the control plane's job; this only holds the hashes, their scope caps, and
/// their lifecycle. A token is looked up for authentication by its hash, and managed (listed, revoked) by its owner.
/// </summary>
public static class AccessTokenStore
{
    /// <summary>Persists a new token row (the secret is already hashed by the caller) and returns its id. The row is
    /// born active; expiry is optional.</summary>
    public static async Task<Guid> CreateAsync(
        CatalogDbContext catalog, Guid userId, string name, string tokenHash, string prefix, string scopes,
        DateTime? expiresUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopes);

        var token = new CatalogAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name.Trim(),
            TokenHash = tokenHash,
            Prefix = prefix,
            Scopes = scopes,
            CreatedUtc = nowUtc,
            ExpiresUtc = expiresUtc,
        };
        catalog.AccessTokens.Add(token);
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        return token.Id;
    }

    /// <summary>One owner's tokens, newest first (active and revoked; the caller renders the state).</summary>
    public static async Task<IReadOnlyList<CatalogAccessToken>> ListForUserAsync(
        CatalogDbContext catalog, Guid userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.AccessTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedUtc).ThenBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Resolves a presented token (by its hash) to the identity and grant it authenticates as, or null when
    /// the token is unknown, revoked, expired, or its owner is inactive or has no provisioned role. Fails closed on
    /// every one of those so a stale or downgraded credential stops working without a sweep of the token rows.</summary>
    public static async Task<AccessTokenAuth?> FindActiveByHashAsync(
        CatalogDbContext catalog, string tokenHash, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        var row = await (
            from t in catalog.AccessTokens.AsNoTracking()
            where t.TokenHash == tokenHash && t.RevokedUtc == null
                && (t.ExpiresUtc == null || t.ExpiresUtc > nowUtc)
            join u in catalog.Users.AsNoTracking() on t.UserId equals u.Id
            where u.Active
            join r in catalog.Roles.AsNoTracking() on u.Role equals r.Name
            select new
            {
                t.Id, t.UserId, u.Username, u.Role, TokenScopes = t.Scopes, RoleScopes = r.Scopes, t.LastUsedUtc,
            }).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null
            ? null
            : new AccessTokenAuth(
                row.Id, row.UserId, row.Username, row.Role,
                Split(row.TokenScopes), Split(row.RoleScopes), row.LastUsedUtc);
    }

    /// <summary>Stamps a token's last-used time. Best-effort telemetry, called only when the stored value is stale,
    /// so an authenticated request does not write on every call.</summary>
    public static Task TouchLastUsedAsync(
        CatalogDbContext catalog, Guid tokenId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.AccessTokens
            .Where(t => t.Id == tokenId)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.LastUsedUtc, nowUtc), ct);
    }

    /// <summary>Revokes a token, but only one the caller owns and only if it is still active. Returns true when a row
    /// was revoked; false when no active token with that id belongs to the user (already revoked, unknown, or another
    /// user's, all indistinguishable to the caller by design). Idempotent: a second revoke is a no-op false. A token the
    /// control plane's configuration sets is never revoked here (see <see cref="ConfiguredByAsync"/>).</summary>
    public static async Task<bool> RevokeAsync(
        CatalogDbContext catalog, Guid userId, Guid tokenId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.AccessTokens
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedUtc == null && t.ConfiguredBy == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>The configuration key that sets the user's active token <paramref name="tokenId"/>, or null for a token someone minted.</summary>
    public static Task<string?> ConfiguredByAsync(CatalogDbContext catalog, Guid userId, Guid tokenId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.AccessTokens.AsNoTracking()
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedUtc == null)
            .Select(t => t.ConfiguredBy)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Makes the token whose secret hashes to <paramref name="tokenHash"/> the one active credential that
    /// <paramref name="setting"/> sets, owned by <paramref name="ownerId"/> with <paramref name="scopes"/>, in one
    /// transaction: issues it when the catalog does not hold it, takes it back into service when it was revoked (the
    /// configuration names it again), and revokes every other token the setting set before (a rotated secret). Answers
    /// what it did, in a few words; doing it again changes nothing.
    /// </summary>
    public static Task<string> EnsureConfiguredAsync(
        CatalogDbContext catalog, string setting, Guid ownerId, string name, string tokenHash, string prefix, string scopes,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopes);
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var revoked = await catalog.AccessTokens
                .Where(t => t.ConfiguredBy == setting && t.TokenHash != tokenHash && t.RevokedUtc == null)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedUtc, nowUtc), ct)
                .ConfigureAwait(false);

            var token = await catalog.AccessTokens.AsTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct).ConfigureAwait(false);
            string outcome;
            if (token is null)
            {
                catalog.AccessTokens.Add(new CatalogAccessToken
                {
                    Id = Guid.NewGuid(),
                    UserId = ownerId,
                    Name = name,
                    TokenHash = tokenHash,
                    Prefix = prefix,
                    Scopes = scopes,
                    CreatedUtc = nowUtc,
                    ConfiguredBy = setting,
                });
                outcome = "issued";
            }
            else if (token.RevokedUtc is not null || token.ConfiguredBy != setting || token.UserId != ownerId
                     || token.Scopes != scopes || token.ExpiresUtc is not null)
            {
                token.RevokedUtc = null;
                token.ExpiresUtc = null;
                token.ConfiguredBy = setting;
                token.UserId = ownerId;
                token.Scopes = scopes;
                token.Name = name;
                outcome = "taken back into service";
            }
            else
            {
                outcome = "unchanged";
            }

            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            return revoked > 0 ? $"{outcome}; {revoked} earlier one(s) revoked" : outcome;
        }, ct);
    }

    /// <summary>Revokes every active token <paramref name="setting"/> set, once the configuration no longer names one. Answers how many.</summary>
    public static Task<int> RevokeConfiguredAsync(CatalogDbContext catalog, string setting, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        return catalog.AccessTokens
            .Where(t => t.ConfiguredBy == setting && t.RevokedUtc == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedUtc, nowUtc), ct);
    }

    private static string[] Split(string scopes)
        => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
