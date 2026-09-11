using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;

namespace SqlFlow.Catalog;

/// <summary>The outcome of a user mutation, so the API can answer 200 / 404 / 409 precisely.</summary>
public enum UserMutation
{
    Applied,
    NotFound,

    /// <summary>The referenced role does not exist in <see cref="CatalogRole"/>.</summary>
    UnknownRole,

    /// <summary>Refused because it would leave the catalog with no active admin (lockout prevention).</summary>
    LastAdmin,

    /// <summary>Refused because the operation only applies to local users (for example a password reset on an
    /// SSO-provisioned user, whose credential lives in Entra, not here).</summary>
    NotLocal,

    /// <summary>Refused because the requested sign-in name already belongs to a different account.</summary>
    UsernameTaken,
}

/// <summary>The outcome of creating a local user.</summary>
public enum UserCreateStatus
{
    Created,
    UsernameTaken,
    UnknownRole,
}

/// <summary>The outcome of an external (Entra) sign-in against the user store.</summary>
public enum ExternalSignInStatus
{
    /// <summary>The user existed and their profile/last-login were refreshed.</summary>
    SignedIn,

    /// <summary>First sign-in: the user was provisioned just-in-time with the default role.</summary>
    Provisioned,

    /// <summary>The user exists but is deactivated; sign-in is refused.</summary>
    Inactive,

    /// <summary>A different account already owns this sign-in name, so provisioning would be ambiguous. The
    /// conflict is surfaced instead of silently linking two identities.</summary>
    UsernameConflict,

    /// <summary>The configured default role for JIT provisioning does not exist (bootstrap has not seeded roles).</summary>
    DefaultRoleMissing,
}

/// <summary>The identity claims an external (Entra) token supplies for JIT provisioning.</summary>
/// <param name="ObjectId">The immutable Entra object id (<c>oid</c> claim): the key JIT provisioning matches on.</param>
/// <param name="Username">The sign-in name (<c>preferred_username</c>, normally the UPN/email).</param>
/// <param name="DisplayName">The human name (<c>name</c> claim), when present.</param>
/// <param name="Email">The email claim, when present.</param>
public sealed record ExternalUserProfile(string ObjectId, string Username, string? DisplayName, string? Email);

/// <summary>
/// Persistence for control-plane users and roles. Stateless like the other catalog stores; password hashing and
/// token issuance are the control plane's job, this only holds identities, role references, and their lifecycle.
/// Mutations that must see a consistent picture (create with a uniqueness check, role/active changes with the
/// last-admin guard) run in one serializable transaction so concurrent admin actions can never race the catalog
/// into a locked-out state.
/// </summary>
public static class UserStore
{
    /// <summary>The user owning this sign-in name, or null. The catalog's default SQL Server collation is
    /// case-insensitive, so lookup matches how the unique index enforces uniqueness.</summary>
    public static async Task<CatalogUser?> FindByUsernameAsync(
        CatalogDbContext catalog, string username, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var normalized = username.Trim();
        return await catalog.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalized, ct).ConfigureAwait(false);
    }

    public static async Task<CatalogUser?> FindByIdAsync(
        CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
    }

    /// <summary>The role row (its scope grants), or null when the role does not exist.</summary>
    public static async Task<CatalogRole?> FindRoleAsync(
        CatalogDbContext catalog, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        return await catalog.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Name == normalized, ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<CatalogRole>> ListRolesAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether any user exists at all (drives the bootstrap "no users provisioned" warning).</summary>
    public static Task<bool> AnyUsersAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Users.AsNoTracking().AnyAsync(ct);
    }

    /// <summary>Seeds a role if it is missing. Existing rows are left untouched (an operator's scope edits are
    /// deliberate state, not drift to correct), so calling this on every startup is safe.</summary>
    public static Task EnsureRoleAsync(
        CatalogDbContext catalog, string name, string scopes, string description, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopes);
        ArgumentNullException.ThrowIfNull(description);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            // Seeding ensures existence and guarantees the code-defined scopes are present; it never manages
            // drift the other way. A scope an operator ADDED to a built-in role survives re-seeding, and a scope
            // the definition gained later (a new surface such as the node protocol) is appended to an existing row,
            // so a catalog seeded before that scope existed does not need it granted by hand.
            var existing = await catalog.Roles.AsTracking()
                .FirstOrDefaultAsync(r => r.Name == name, ct).ConfigureAwait(false);
            if (existing is null)
            {
                catalog.Roles.Add(new CatalogRole
                {
                    Name = name,
                    Scopes = scopes,
                    Description = description,
                    CreatedUtc = nowUtc,
                });
                return false;
            }

            var current = existing.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var missing = scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !current.Contains(s, StringComparer.Ordinal))
                .ToList();
            if (missing.Count > 0)
            {
                existing.Scopes = string.Join(' ', current.Concat(missing));
            }

            return true;
        }, ct);
    }

    /// <summary>Creates a local (username + password hash) user. The username uniqueness check and the insert run
    /// in one serializable transaction, so a concurrent create of the same name cannot slip past the check; the
    /// unique index is the backstop.</summary>
    public static Task<(UserCreateStatus Status, Guid Id)> CreateLocalAsync(
        CatalogDbContext catalog, string username, string passwordHash, string role, string? email,
        string? displayName, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var normalizedUsername = username.Trim();
        var normalizedRole = role.Trim();
        // Deterministic from the sign-in name (case-folded, since the unique index is case-insensitive), matching
        // the catalog's identity convention, so re-provisioning the same admin never mints a second id.
        var derivedId = FlowIdentity.FromName($"user/local/{normalizedUsername.ToUpperInvariant()}");

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var roleExists = await catalog.Roles.AnyAsync(r => r.Name == normalizedRole, ct).ConfigureAwait(false);
            if (!roleExists)
            {
                return (UserCreateStatus.UnknownRole, Guid.Empty);
            }

            var taken = await catalog.Users.AnyAsync(u => u.Username == normalizedUsername, ct).ConfigureAwait(false);
            if (taken)
            {
                return (UserCreateStatus.UsernameTaken, Guid.Empty);
            }

            // The derivation belongs to the NAME, and a rename frees a name (see UpdateProfileAsync). When an
            // earlier holder of this name still owns the derived id, mint a fresh one: a row's identity is its id,
            // and re-using a taken key would fail the insert on the primary key.
            var id = derivedId;
            if (await catalog.Users.AnyAsync(u => u.Id == derivedId, ct).ConfigureAwait(false))
            {
                id = Guid.NewGuid();
            }

            catalog.Users.Add(new CatalogUser
            {
                Id = id,
                Username = normalizedUsername,
                Email = NormalizeOptional(email),
                DisplayName = NormalizeOptional(displayName),
                PasswordHash = passwordHash,
                Role = normalizedRole,
                Provider = UserProviders.Local,
                Active = true,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            });
            return (UserCreateStatus.Created, id);
        }, ct);
    }

    /// <summary>
    /// Signs in an external (Entra) identity, provisioning it just-in-time on first sight. Matching is by the
    /// immutable object id, so a UPN rename updates the row instead of duplicating it; when the renamed UPN is
    /// already owned by a different account the old sign-in name is kept (never silently merge identities) and the
    /// sign-in still succeeds. A deactivated user is refused.
    /// </summary>
    public static Task<(ExternalSignInStatus Status, CatalogUser? User)> EnsureExternalAsync(
        CatalogDbContext catalog, ExternalUserProfile profile, string defaultRole, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ObjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRole);

        var objectId = profile.ObjectId.Trim();
        var username = profile.Username.Trim();

        return CatalogTransaction.InSerializableAsync<(ExternalSignInStatus, CatalogUser?)>(catalog, async () =>
        {
            var existing = await catalog.Users.AsTracking()
                .FirstOrDefaultAsync(u => u.ExternalObjectId == objectId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.Active)
                {
                    catalog.ChangeTracker.Clear(); // persist nothing for a refused sign-in
                    return (ExternalSignInStatus.Inactive, existing);
                }

                if (!string.Equals(existing.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    var usernameOwned = await catalog.Users
                        .AnyAsync(u => u.Id != existing.Id && u.Username == username, ct).ConfigureAwait(false);
                    if (!usernameOwned)
                    {
                        existing.Username = username;
                    }
                }

                existing.DisplayName = NormalizeOptional(profile.DisplayName) ?? existing.DisplayName;
                existing.Email = NormalizeOptional(profile.Email) ?? existing.Email;
                existing.LastLoginUtc = nowUtc;
                existing.UpdatedUtc = nowUtc;
                return (ExternalSignInStatus.SignedIn, existing);
            }

            var roleExists = await catalog.Roles.AnyAsync(r => r.Name == defaultRole, ct).ConfigureAwait(false);
            if (!roleExists)
            {
                return (ExternalSignInStatus.DefaultRoleMissing, null);
            }

            var taken = await catalog.Users.AnyAsync(u => u.Username == username, ct).ConfigureAwait(false);
            if (taken)
            {
                return (ExternalSignInStatus.UsernameConflict, null);
            }

            var user = new CatalogUser
            {
                Id = FlowIdentity.FromName($"user/entra/{objectId}"),
                Username = username,
                Email = NormalizeOptional(profile.Email),
                DisplayName = NormalizeOptional(profile.DisplayName),
                PasswordHash = null,
                Role = defaultRole,
                Provider = UserProviders.Entra,
                ExternalObjectId = objectId,
                Active = true,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                LastLoginUtc = nowUtc,
            };
            catalog.Users.Add(user);
            return (ExternalSignInStatus.Provisioned, user);
        }, ct);
    }

    /// <summary>Stamps a successful local sign-in.</summary>
    public static Task RecordLoginAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Users
            .Where(u => u.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.LastLoginUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Replaces a local user's password hash (admin reset, or an upgrade rehash after verification).
    /// Refused for SSO users, whose credential lives in the external provider.</summary>
    public static Task<UserMutation> SetPasswordHashAsync(
        CatalogDbContext catalog, Guid id, string passwordHash, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var user = await catalog.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
            if (user is null)
            {
                return UserMutation.NotFound;
            }

            if (user.Provider != UserProviders.Local)
            {
                catalog.ChangeTracker.Clear();
                return UserMutation.NotLocal;
            }

            user.PasswordHash = passwordHash;
            user.UpdatedUtc = nowUtc;
            return UserMutation.Applied;
        }, ct);
    }

    /// <summary>
    /// Renames a user and rewrites their profile fields. The sign-in name may only change for a local user: an SSO
    /// user's username is the Entra UPN their token carries, refreshed on every sign-in, so an edit here would be
    /// silently reverted. Display name and email are editable for either kind (an SSO sign-in refreshes whichever
    /// of those claims its token carries), and a null clears the stored value.
    /// </summary>
    public static Task<UserMutation> UpdateProfileAsync(
        CatalogDbContext catalog, Guid id, string username, string? email, string? displayName, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var normalizedUsername = username.Trim();

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var user = await catalog.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
            if (user is null)
            {
                return UserMutation.NotFound;
            }

            // Ordinal, so correcting only the casing of a name is still a real change; the uniqueness probe below
            // excludes this row, so a case-only rename never collides with itself under the case-insensitive index.
            if (!string.Equals(user.Username, normalizedUsername, StringComparison.Ordinal))
            {
                if (user.Provider != UserProviders.Local)
                {
                    catalog.ChangeTracker.Clear();
                    return UserMutation.NotLocal;
                }

                var taken = await catalog.Users
                    .AnyAsync(u => u.Id != id && u.Username == normalizedUsername, ct).ConfigureAwait(false);
                if (taken)
                {
                    catalog.ChangeTracker.Clear();
                    return UserMutation.UsernameTaken;
                }

                user.Username = normalizedUsername;
            }

            user.Email = NormalizeOptional(email);
            user.DisplayName = NormalizeOptional(displayName);
            user.UpdatedUtc = nowUtc;
            return UserMutation.Applied;
        }, ct);
    }

    /// <summary>
    /// Removes a user outright, along with the rows only they could ever see: their access tokens (a surviving one
    /// would be a live credential), their notification subscriptions and delivery history, and their chat
    /// conversations. Deleting the last active admin is refused by the same lockout guard the role and active
    /// mutations use. Run history and activity events are deliberately untouched: they record the actor by name, so
    /// what a departed user did stays attributable once the account is gone. Deactivation remains the softer option
    /// when the account may come back.
    /// </summary>
    public static Task<UserMutation> DeleteAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var user = await catalog.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
            if (user is null)
            {
                return UserMutation.NotFound;
            }

            if (user.Active && user.Role == RoleNames.Admin)
            {
                var otherActiveAdmins = await catalog.Users
                    .AnyAsync(u => u.Id != id && u.Active && u.Role == RoleNames.Admin, ct).ConfigureAwait(false);
                if (!otherActiveAdmins)
                {
                    return UserMutation.LastAdmin;
                }
            }

            var conversationIds = catalog.ChatConversations.Where(c => c.UserId == id).Select(c => c.Id);
            await catalog.ChatMessages.Where(m => conversationIds.Contains(m.ConversationId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.ChatConversations.Where(c => c.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.NotificationDeliveries.Where(d => d.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.NotificationSubscriptions.Where(s => s.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.AccessTokens.Where(t => t.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return UserMutation.Applied;
        }, ct);
    }

    /// <summary>Changes a user's role. Demoting the last active admin is refused so the catalog can never lose all
    /// administrative access.</summary>
    public static Task<UserMutation> SetRoleAsync(
        CatalogDbContext catalog, Guid id, string role, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var normalizedRole = role.Trim();

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var user = await catalog.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
            if (user is null)
            {
                return UserMutation.NotFound;
            }

            var roleExists = await catalog.Roles.AnyAsync(r => r.Name == normalizedRole, ct).ConfigureAwait(false);
            if (!roleExists)
            {
                catalog.ChangeTracker.Clear();
                return UserMutation.UnknownRole;
            }

            if (user.Active && user.Role == RoleNames.Admin && normalizedRole != RoleNames.Admin)
            {
                var otherActiveAdmins = await catalog.Users
                    .AnyAsync(u => u.Id != id && u.Active && u.Role == RoleNames.Admin, ct).ConfigureAwait(false);
                if (!otherActiveAdmins)
                {
                    catalog.ChangeTracker.Clear();
                    return UserMutation.LastAdmin;
                }
            }

            user.Role = normalizedRole;
            user.UpdatedUtc = nowUtc;
            return UserMutation.Applied;
        }, ct);
    }

    /// <summary>Activates or deactivates a user. Deactivating the last active admin is refused (lockout
    /// prevention). Deactivation is the catalog's delete: the row (and every run/audit reference to the subject)
    /// stays attributable.</summary>
    public static Task<UserMutation> SetActiveAsync(
        CatalogDbContext catalog, Guid id, bool active, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var user = await catalog.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
            if (user is null)
            {
                return UserMutation.NotFound;
            }

            if (!active && user.Active && user.Role == RoleNames.Admin)
            {
                var otherActiveAdmins = await catalog.Users
                    .AnyAsync(u => u.Id != id && u.Active && u.Role == RoleNames.Admin, ct).ConfigureAwait(false);
                if (!otherActiveAdmins)
                {
                    catalog.ChangeTracker.Clear();
                    return UserMutation.LastAdmin;
                }
            }

            user.Active = active;
            user.UpdatedUtc = nowUtc;
            return UserMutation.Applied;
        }, ct);
    }

    /// <summary>One page of users with the filterable dimensions, name-ordered for a stable listing.</summary>
    public static async Task<(IReadOnlyList<CatalogUser> Items, long Total)> ListAsync(
        CatalogDbContext catalog, int page, int pageSize, string? username, string? role, string? provider,
        bool? active, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var query = catalog.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(username))
        {
            var term = username.Trim();
            query = query.Where(u => u.Username.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var term = role.Trim();
            query = query.Where(u => u.Role == term);
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            var term = provider.Trim();
            query = query.Where(u => u.Provider == term);
        }

        if (active is not null)
        {
            query = query.Where(u => u.Active == active);
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(u => u.Username).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return (items, total);
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
