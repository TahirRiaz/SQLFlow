using Microsoft.AspNetCore.Identity;

namespace SqlFlow.Catalog;

/// <summary>
/// The one place local (username + password) credential rules live, so every caller agrees on how a password is
/// judged and hashed. The control plane hashes through its DI-registered <see cref="IPasswordHasher{TUser}"/>; the
/// CLI's offline <c>user</c> command has no DI container, so it hashes here. Both are the default
/// <see cref="PasswordHasher{TUser}"/> with no custom options, so a hash written by one verifies under the other:
/// the stored hash is self-describing (it embeds its format version and iteration count), and login reads those
/// back when verifying. Keeping the policy constant here too means the length rule cannot drift between the API and
/// the CLI.
/// </summary>
public static class LocalPasswords
{
    /// <summary>Local passwords must be at least this long. Length is the one composition rule that measurably
    /// helps; character-class rules are deliberately not imposed.</summary>
    public const int MinLength = 12;

    // Stateless and thread-safe once constructed (it only reads its options), so a single shared instance serves
    // every hash without per-call allocation.
    private static readonly PasswordHasher<CatalogUser> Hasher = new();

    /// <summary>Produces the storable hash for <paramref name="password"/>, in the exact format login verifies
    /// against. The user is passed through because the hasher's contract takes it, though the default PBKDF2 hasher
    /// does not fold any user field into the hash.</summary>
    public static string Hash(CatalogUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hasher.HashPassword(user, password);
    }
}
