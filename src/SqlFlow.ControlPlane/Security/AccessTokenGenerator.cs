using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Mints and hashes personal access token secrets. A secret is <see cref="Prefix"/> followed by 256 bits of
/// cryptographic randomness in URL-safe base64: the prefix makes a leaked token recognizable (and lets the auth
/// pipeline route it to the PAT handler without a database round-trip), and the 256-bit body makes it unguessable.
/// Only the SHA-256 <c>Hash</c> of the full secret is ever persisted; the cleartext is returned once to the
/// creator and then unrecoverable. Because the body is already high-entropy, a fast unsalted hash is correct here:
/// there is nothing to brute-force, and it keeps authentication a single indexed lookup.
/// </summary>
public static class AccessTokenGenerator
{
    /// <summary>The literal that opens every SQLFlow PAT, so a secret is self-identifying in logs and in the auth
    /// pipeline's scheme selector.</summary>
    public const string Prefix = "sqlf_";

    /// <summary>How many leading characters of the secret are kept for display (prefix plus a few body chars), so an
    /// owner can recognize a token in the list without the unrecoverable full value.</summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>A freshly minted token: the one-time <paramref name="Secret"/> the creator must copy now, the
    /// <paramref name="Hash"/> to persist, and the non-secret <paramref name="DisplayPrefix"/> for the listing.</summary>
    public readonly record struct MintedToken(string Secret, string Hash, string DisplayPrefix);

    /// <summary>Generates a new secret and its stored derivatives. Uses a cryptographic RNG; never reuse the secret
    /// after handing it back once.</summary>
    public static MintedToken Mint()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var secret = Prefix + Base64UrlEncode(bytes);
        return new MintedToken(secret, HashSecret(secret), secret[..DisplayPrefixLength]);
    }

    /// <summary>The lowercase-hex SHA-256 of a full secret: the value stored, and what a presented token is hashed to
    /// for lookup.</summary>
    public static string HashSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Whether a bearer value is shaped like a SQLFlow PAT (so the auth pipeline routes it here rather than
    /// to JWT validation). A cheap prefix test, not a validation; the hash lookup is the real check.</summary>
    public static bool LooksLikeToken(string? value)
        => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
