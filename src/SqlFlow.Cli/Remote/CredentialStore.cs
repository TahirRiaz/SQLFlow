using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core;

namespace SqlFlow.Cli.Remote;

/// <summary>One stored control-plane credential: the personal access token, its server-side id (so logout can
/// revoke it; null when the token was pasted and its id could not be matched), and who minted it.</summary>
internal sealed record StoredCredential(
    string Token, Guid? TokenId, string? Username, DateTime CreatedUtc, DateTime? ExpiresUtc);

/// <summary>
/// The CLI's credential file: <c>~/.sqlflow/credentials.json</c>, a map from normalized control-plane URL to a
/// personal access token. Only PATs land here (revocable server-side at any time); a password is never written
/// anywhere, and short-lived session JWTs are not persisted. On Unix the file is chmod 600; on Windows the
/// user-profile directory's ACL is the protection (the same model the .NET SDK and gh CLI use). Precedence at
/// use time is --token, then SQLFLOW_TOKEN (which the git-ignored .sqlflow/env can supply), then this store,
/// so automation never needs the file.
/// </summary>
internal static class CredentialStore
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The credential file's location: <c>SQLFLOW_CREDENTIALS_FILE</c> when set (containers and CI
    /// mount their secret store wherever they like, and tests isolate themselves from the operator's own
    /// file), else under the user profile so it is per-user and off the repo.</summary>
    internal static string FilePath
        => Environment.GetEnvironmentVariable("SQLFLOW_CREDENTIALS_FILE") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sqlflow", "credentials.json");

    /// <summary>The stored credential for a control-plane URL, or null when none is stored.</summary>
    public static StoredCredential? Load(Uri url)
    {
        var credentials = ReadAll();
        return credentials.TryGetValue(Normalize(url), out var credential) ? credential : null;
    }

    /// <summary>Stores (or replaces) the credential for a URL.</summary>
    public static void Save(Uri url, StoredCredential credential)
    {
        var credentials = ReadAll();
        credentials[Normalize(url)] = credential;
        WriteAll(credentials);
    }

    /// <summary>Removes the credential for a URL. False when nothing was stored for it.</summary>
    public static bool Remove(Uri url)
    {
        var credentials = ReadAll();
        if (!credentials.Remove(Normalize(url)))
        {
            return false;
        }

        WriteAll(credentials);
        return true;
    }

    /// <summary>The stable per-URL key: scheme and host case-insensitive, default ports elided by
    /// <see cref="Uri"/> itself, trailing slash trimmed, so <c>https://Host/</c> and <c>https://host</c> are
    /// one entry.</summary>
    internal static string Normalize(Uri url)
        => url.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    private static Dictionary<string, StoredCredential> ReadAll()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlFlowException($"Could not read the credential file {path}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, StoredCredential>>(text, FileJson)
                   ?? new Dictionary<string, StoredCredential>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            // A corrupted file must not brick every remote verb with a cryptic parse error; name the file and
            // the recovery (delete it and log in again).
            throw new SqlFlowException(
                $"The credential file {path} is not valid JSON ({ex.Message}); delete it and run 'sqlflow login' again.");
        }
    }

    private static void WriteAll(Dictionary<string, StoredCredential> credentials)
    {
        var path = FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!OperatingSystem.IsWindows())
            {
                // Create-then-restrict leaves a window where the default umask applies; create the file with
                // owner-only mode up front instead, then write through the handle.
                using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                using var writer = new StreamWriter(stream);
                writer.Write(JsonSerializer.Serialize(credentials, FileJson));
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(credentials, FileJson));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SqlFlowException($"Could not write the credential file {path}: {ex.Message}");
        }
    }
}
