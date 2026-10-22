using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>
/// Where a GitHub App's private key is read from: the PEM text (or the base64 of it) held by an environment variable, a
/// file holding the PEM, or a <c>${scheme:locator}</c> reference resolved by the registered secret providers (most often
/// <c>${keyvault:vault/secret}</c>). The key is read each time a token is minted, so a key rotated in the vault takes
/// effect with the next token. <see cref="Description"/> says where the key comes from without ever carrying it, which is
/// what every message and status shows.
/// </summary>
public sealed partial class GitHubAppKeySource
{
    /// <summary>The largest key material accepted: a 4096-bit PEM key is about 3.3 KB, so anything larger is not a key.</summary>
    internal const int MaxMaterialLength = 16 * 1024;

    private readonly KeyOrigin _origin;
    private readonly string _value;

    private GitHubAppKeySource(KeyOrigin origin, string value, string description, string identity)
    {
        _origin = origin;
        _value = value;
        Description = description;
        Identity = identity;
    }

    private enum KeyOrigin
    {
        /// <summary>The PEM text, or the base64 of it, held in memory as an environment variable gave it.</summary>
        Inline,

        /// <summary>A file holding the PEM text.</summary>
        File,

        /// <summary>A whole <c>${scheme:locator}</c> reference.</summary>
        Reference,
    }

    /// <summary>Where the key comes from, for messages and status: a variable's name, a file's path, or a reference. Never the key.</summary>
    public string Description { get; }

    /// <summary>A secret-free identity of the source, so tokens minted with one key are never served for another.</summary>
    internal string Identity { get; }

    /// <summary>The reference the key is read through, or null when it is read from a variable's value or a file.</summary>
    public string? Reference => _origin == KeyOrigin.Reference ? _value : null;

    /// <summary>
    /// The key held by the environment variable <paramref name="name"/>, whose value is <paramref name="value"/>: a whole
    /// <c>${scheme:locator}</c> reference to the key, the PEM text (with real line breaks or <c>\n</c> escapes), or the
    /// base64 of the PEM text (how a key fits a one-line env file). The form is checked here, so a variable holding
    /// something else fails at configuration rather than at the first token.
    /// </summary>
    /// <exception cref="GitHubAppException">The value is none of the accepted forms.</exception>
    public static GitHubAppKeySource FromVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        if (WholeReference().IsMatch(trimmed))
        {
            return new GitHubAppKeySource(KeyOrigin.Reference, trimmed, $"{name} ({trimmed})", "reference:" + trimmed);
        }

        // Validated now, so a misconfigured host says so in its status and its first sync, naming the variable. The
        // material is not kept in any message: a value that is not a key may still be a secret pasted in the wrong place.
        _ = GitHubAppPrivateKey.ToPem(trimmed, name);
        return new GitHubAppKeySource(KeyOrigin.Inline, trimmed, name, "variable:" + name);
    }

    /// <summary>The key in the PEM file at <paramref name="path"/>, which must be absolute: a host's working directory
    /// is not something its configuration can rely on.</summary>
    /// <exception cref="GitHubAppException">The path is not absolute.</exception>
    public static GitHubAppKeySource FromFile(string variable, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var trimmed = path.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new GitHubAppException(
                $"{variable} must be an absolute path to the GitHub App's .pem file; '{trimmed}' is relative.");
        }

        return new GitHubAppKeySource(KeyOrigin.File, trimmed, $"{variable} ({trimmed})", "file:" + trimmed);
    }

    /// <summary>The key a <c>${scheme:locator}</c> reference resolves to, named in messages as <paramref name="description"/>.</summary>
    public static GitHubAppKeySource FromReference(string reference, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new GitHubAppKeySource(KeyOrigin.Reference, reference, description, "reference:" + reference);
    }

    /// <summary>
    /// Reads the key and imports it. The caller disposes the key. Every failure names the App and this source, and
    /// carries the cause's message with credential fragments redacted.
    /// </summary>
    /// <exception cref="GitHubAppException">The key could not be read, or what was read is not an RSA private key.</exception>
    internal async Task<RSA> LoadAsync(string appId, ISecretResolver resolver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        string material;
        switch (_origin)
        {
            case KeyOrigin.Inline:
                material = _value;
                break;

            case KeyOrigin.File:
                material = await ReadFileAsync(appId, ct).ConfigureAwait(false);
                break;

            default:
                try
                {
                    material = await resolver.ResolveAsync(_value, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new GitHubAppException(
                        $"could not read the private key of GitHub App {appId} from {Description}: {SecretHygiene.RedactedMessage(ex)}", ex);
                }

                break;
        }

        return GitHubAppPrivateKey.Import(material, $"of GitHub App {appId} from {Description}");
    }

    private async Task<string> ReadFileAsync(string appId, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(_value);
            if (!info.Exists)
            {
                throw new GitHubAppException(
                    $"the private key file of GitHub App {appId} does not exist: {Description}. Mount the App's .pem file there, or point the variable at it.");
            }

            if (info.Length > MaxMaterialLength)
            {
                throw new GitHubAppException(
                    $"the private key file of GitHub App {appId} ({Description}) is {info.Length} bytes, too large for a private key; point the variable at the App's .pem file.");
            }

            return await File.ReadAllTextAsync(_value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new GitHubAppException(
                $"could not read the private key file of GitHub App {appId} ({Description}): {ex.Message}", ex);
        }
    }

    /// <summary>The source's description; never the key.</summary>
    public override string ToString() => Description;

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeReference();
}

/// <summary>
/// Turns key material into an RSA private key: the PEM GitHub hands out (PKCS#1, <c>BEGIN RSA PRIVATE KEY</c>) or its
/// PKCS#8 conversion (<c>BEGIN PRIVATE KEY</c>), with real line breaks or <c>\n</c> escapes, wrapped in quotes or not,
/// or the base64 of either. What is not such a key is refused with what it looks like instead (encrypted, public, a
/// certificate, an EC or OpenSSH key), and no message ever carries the material.
/// </summary>
internal static class GitHubAppPrivateKey
{
    private const string PemStart = "-----BEGIN ";

    /// <summary>The smallest key GitHub issues or accepts for an App.</summary>
    private const int MinimumKeySize = 2048;

    /// <summary>The PEM text of <paramref name="material"/>, described in messages as <paramref name="origin"/>.</summary>
    /// <exception cref="GitHubAppException">The material is empty, too long, or neither PEM text nor base64 of it.</exception>
    internal static string ToPem(string material, string origin)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.Length > GitHubAppKeySource.MaxMaterialLength)
        {
            throw new GitHubAppException(
                $"the GitHub App private key {origin} is {material.Length} characters, too long for a private key.");
        }

        var text = Unquote(material.Trim().TrimStart('﻿').Trim());
        if (text.Length == 0)
        {
            throw new GitHubAppException($"the GitHub App private key {origin} is empty.");
        }

        // A key written on one line with \n escapes (the way most env files and CI variables carry it).
        if (!text.Contains('\n', StringComparison.Ordinal) && text.Contains("\\n", StringComparison.Ordinal))
        {
            text = text.Replace("\\r\\n", "\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal);
        }

        if (text.StartsWith(PemStart, StringComparison.Ordinal))
        {
            return text;
        }

        // The base64 of the PEM file: what fits a one-line variable without escapes.
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var buffer = new byte[compact.Length];
        if (compact.Length > 0 && Convert.TryFromBase64String(compact, buffer, out var written))
        {
            var decoded = Encoding.UTF8.GetString(buffer, 0, written).TrimStart('﻿').Trim();
            if (decoded.StartsWith(PemStart, StringComparison.Ordinal))
            {
                return decoded;
            }
        }

        throw new GitHubAppException(
            $"the GitHub App private key {origin} is not a PEM key: give the contents of the App's .pem file (starting "
            + "'-----BEGIN RSA PRIVATE KEY-----', with line breaks or \\n escapes), the base64 of that file, or a ${...} reference to either.");
    }

    /// <summary>The RSA private key <paramref name="material"/> holds. The caller disposes it.</summary>
    /// <exception cref="GitHubAppException">The material is not an unencrypted RSA private key of at least 2048 bits.</exception>
    internal static RSA Import(string material, string origin)
    {
        var pem = ToPem(material, origin);
        var label = Label(pem);
        switch (label)
        {
            case "RSA PRIVATE KEY" or "PRIVATE KEY":
                break;
            case "ENCRYPTED PRIVATE KEY":
                throw Encrypted(origin);
            case "RSA PUBLIC KEY" or "PUBLIC KEY":
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is a public key; give the private key GitHub generated for the App (its .pem download).");
            case "CERTIFICATE":
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is a certificate; give the private key GitHub generated for the App (its .pem download).");
            case "EC PRIVATE KEY":
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is an EC key; GitHub Apps sign with RSA, so give the App's own .pem download.");
            case "OPENSSH PRIVATE KEY":
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is in OpenSSH format; give the App's own .pem download (PEM, 'BEGIN RSA PRIVATE KEY').");
            default:
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is a PEM block of type '{label}', not a private key; give the App's own .pem download.");
        }

        if (pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase))
        {
            throw Encrypted(origin);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < MinimumKeySize)
            {
                throw new GitHubAppException(
                    $"the GitHub App private key {origin} is a {rsa.KeySize}-bit key; GitHub App keys are {MinimumKeySize} bits or more, so this is not the App's key.");
            }

            return rsa;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new GitHubAppException($"the GitHub App private key {origin} is not a valid RSA private key: {ex.Message}", ex);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static GitHubAppException Encrypted(string origin)
        => new($"the GitHub App private key {origin} is encrypted with a passphrase; store the key as GitHub generated it (unencrypted) in the vault instead.");

    /// <summary>The type of the first PEM block: the text between <c>-----BEGIN </c> and the closing dashes.</summary>
    private static string Label(string pem)
    {
        var start = PemStart.Length;
        var end = pem.IndexOf("-----", start, StringComparison.Ordinal);
        return end > start ? pem[start..end].Trim() : string.Empty;
    }

    /// <summary>One pair of matching quotes around the whole value is removed: env files and CI variables often keep them.</summary>
    private static string Unquote(string value)
        => value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]
            ? value[1..^1].Trim()
            : value;
}
