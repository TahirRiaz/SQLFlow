using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>A GitHub repository: its owner (a user or an organization) and its name.</summary>
public sealed partial record GitHubRepositoryName
{
    public GitHubRepositoryName(string owner, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Owner = owner;
        Name = name;
    }

    /// <summary>The user or organization owning the repository.</summary>
    public string Owner { get; }

    /// <summary>The repository's name, without <c>.git</c>.</summary>
    public string Name { get; }

    /// <summary>Reads <c>owner/name</c> (a trailing <c>.git</c> is dropped), or returns null when the value is not a
    /// GitHub owner and repository name.</summary>
    public static GitHubRepositoryName? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split('/');
        if (parts.Length != 2)
        {
            return null;
        }

        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return OwnerPattern().IsMatch(parts[0]) && NamePattern().IsMatch(name) && name is not "." and not ".."
            ? new GitHubRepositoryName(parts[0], name)
            : null;
    }

    public override string ToString() => $"{Owner}/{Name}";

    // GitHub's own rules: an owner is letters, digits and single dashes (at most 39 characters); a repository name is
    // letters, digits, dots, dashes and underscores (at most 100 characters).
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>The repository permissions git operations need from a GitHub App installation token.</summary>
public static class GitHubAppPermissions
{
    /// <summary>Clone, fetch and list a repository's branches.</summary>
    public static readonly IReadOnlyDictionary<string, string> ReadContents =
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" };

    /// <summary>Push a branch (and remove it again) and open a pull request from it.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProposeChanges =
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "write", ["pull_requests"] = "write" };

    /// <summary>Push commits to a branch.</summary>
    public static readonly IReadOnlyDictionary<string, string> PushContents =
        new SortedDictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "write" };

    /// <summary>The permissions as a reference writes them: <c>contents:read,pull_requests:write</c>.</summary>
    public static string Format(IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return string.Join(',', permissions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"));
    }
}

/// <summary>
/// What to mint an installation token for: the App (and its key), the GitHub API, the installation (given, or found
/// from the repository), the repository the token is narrowed to, and the permissions it is narrowed to. Built from a
/// <c>${githubapp:...}</c> reference by <see cref="GitHubAppReference.Parse(string, GitHubAppSettings)"/>.
/// </summary>
public sealed class GitHubAppTokenRequest
{
    internal GitHubAppTokenRequest(
        GitHubAppIdentity app, string apiRoot, long? installationId, GitHubRepositoryName? repository,
        IReadOnlyDictionary<string, string> permissions)
    {
        App = app;
        ApiRoot = apiRoot;
        InstallationId = installationId;
        Repository = repository;
        Permissions = permissions;
        CacheKey = string.Join(
            '|', apiRoot.ToLowerInvariant(), app.AppId, app.Key.Identity,
            installationId?.ToString(CultureInfo.InvariantCulture) ?? "-",
            repository?.ToString().ToLowerInvariant() ?? "-",
            GitHubAppPermissions.Format(permissions));
    }

    /// <summary>The App the token is minted as.</summary>
    public GitHubAppIdentity App { get; }

    /// <summary>The GitHub REST API root, without a trailing slash.</summary>
    public string ApiRoot { get; }

    /// <summary>The installation to mint on, or null to find the one that holds <see cref="Repository"/>.</summary>
    public long? InstallationId { get; }

    /// <summary>The one repository the token reaches, or null for every repository the installation holds.</summary>
    public GitHubRepositoryName? Repository { get; }

    /// <summary>The permissions the token is narrowed to (name to read, write or admin), or empty for the installation's own.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; }

    /// <summary>Equal for requests that mint the same token, and secret-free.</summary>
    internal string CacheKey { get; }

    /// <summary>What the token is for, in words: the App, the installation or repository, and the permissions.</summary>
    public string Describe()
    {
        var builder = new StringBuilder($"GitHub App {App.AppId}");
        if (InstallationId is { } installation)
        {
            builder.Append(CultureInfo.InvariantCulture, $", installation {installation}");
        }

        if (Repository is { } repository)
        {
            builder.Append(CultureInfo.InvariantCulture, $", repository {repository}");
        }

        if (Permissions.Count > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $", {GitHubAppPermissions.Format(Permissions)}");
        }

        return builder.ToString();
    }

    public override string ToString() => Describe();
}

/// <summary>
/// The <c>${githubapp:...}</c> secret reference: it resolves to a short-lived installation token of a GitHub App, minted
/// when it is resolved, so a credential reference can name an App instead of a personal access token. The locator is
/// <c>name=value</c> settings separated by <c>;</c>:
/// <list type="table">
/// <item><term><c>app</c></term><description>The App's client ID or App ID.</description></item>
/// <item><term><c>key</c></term><description>A reference to the App's private key, written without its <c>${ }</c>:
/// <c>keyvault:vault/secret</c> or <c>env:NAME</c>.</description></item>
/// <item><term><c>installation</c></term><description>The installation's numeric id.</description></item>
/// <item><term><c>repository</c></term><description><c>owner/name</c>: the token reaches this repository alone, and when
/// <c>installation</c> is not given the installation holding it is found.</description></item>
/// <item><term><c>permissions</c></term><description><c>name:level</c> pairs separated by commas
/// (<c>contents:read</c>); the token gets these alone.</description></item>
/// <item><term><c>api</c></term><description>The GitHub API root (GitHub Enterprise Server, GHE.com).</description></item>
/// </list>
/// <c>app</c> and <c>key</c> are given together or not at all; left out, they are the host's App
/// (<see cref="GitHubAppEnvironment"/>). <c>api</c> left out is the host's API. One of <c>installation</c> and
/// <c>repository</c> is needed; a repository source's credential completes a reference that names neither with its own
/// repository (<see cref="Complete"/>).
/// </summary>
public static partial class GitHubAppReference
{
    /// <summary>The reference scheme: <c>${githubapp:...}</c>.</summary>
    public const string Scheme = "githubapp";

    /// <summary>The longest locator accepted; a reference names an App, a key, a repository and a few permissions.</summary>
    internal const int MaxLocatorLength = 1024;

    private const string Prefix = "${" + Scheme + ":";

    private static readonly string[] Names = ["app", "key", "installation", "repository", "permissions", "api"];

    private static readonly HashSet<string> Levels = new(StringComparer.Ordinal) { "read", "write", "admin" };

    /// <summary>Whether <paramref name="value"/> is a whole <c>${githubapp:...}</c> reference.</summary>
    public static bool IsReference(string? value)
        => value is not null
           && value.Trim() is var trimmed
           && trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
           && trimmed.EndsWith('}')
           && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1;

    /// <summary>The locator of a whole <c>${githubapp:...}</c> reference.</summary>
    /// <exception cref="ArgumentException">The value is not such a reference.</exception>
    public static string LocatorOf(string reference)
        => IsReference(reference)
            ? reference.Trim()[Prefix.Length..^1]
            : throw new ArgumentException($"'{reference}' is not a ${{{Scheme}:...}} reference.", nameof(reference));

    /// <summary>The reference to a token of the host's App, narrowed to <paramref name="repository"/> and <paramref name="permissions"/>.</summary>
    public static string ForRepository(GitHubRepositoryName repository, IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(permissions);
        var locator = $"repository={repository}";
        return permissions.Count == 0
            ? $"{Prefix}{locator}}}"
            : $"{Prefix}{locator};permissions={GitHubAppPermissions.Format(permissions)}}}";
    }

    /// <summary>
    /// Completes a repository source's <c>${githubapp:...}</c> reference for an operation on <paramref name="repository"/>:
    /// a reference that names no repository is narrowed to it, and one that names no permissions to
    /// <paramref name="permissions"/>, so a token minted for a source reaches that source's repository alone and does only
    /// what the operation needs. What the reference names itself is kept.
    /// </summary>
    /// <exception cref="GitHubAppException">The reference is malformed.</exception>
    public static string Complete(string reference, GitHubRepositoryName repository, IReadOnlyDictionary<string, string> permissions)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(permissions);
        var locator = LocatorOf(reference);
        var settings = Settings(locator, reference);
        var completed = locator.Trim().TrimEnd(';');
        if (!settings.ContainsKey("repository"))
        {
            completed += $";repository={repository}";
        }

        if (!settings.ContainsKey("permissions") && permissions.Count > 0)
        {
            completed += $";permissions={GitHubAppPermissions.Format(permissions)}";
        }

        return $"{Prefix}{completed}}}";
    }

    /// <summary>
    /// A <c>${githubapp:...}</c> reference made ready for an operation on the git remote <paramref name="remoteUrl"/>:
    /// narrowed to the remote's repository and to <paramref name="permissions"/> where it names neither
    /// (<see cref="Complete"/>). A reference naming another repository than the remote's, or naming no installation or
    /// repository for a remote its GitHub does not serve, is refused: a token for it could not reach the remote.
    /// </summary>
    /// <exception cref="GitHubAppException">The reference is malformed or cannot serve the remote, naming what to change.</exception>
    public static string ForRemote(
        string reference, string? remoteUrl, IReadOnlyDictionary<string, string> permissions, GitHubAppSettings host)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(host);
        var request = Parse(LocatorOf(reference), host, requireTarget: false);
        var repository = RepositoryOn(request.ApiRoot, remoteUrl);
        if (repository is null)
        {
            if (request.InstallationId is null && request.Repository is null)
            {
                throw new GitHubAppException(
                    $"the reference {reference} names no repository, and the remote '{remoteUrl}' is not an https repository on "
                    + $"{GitHubAppSettings.WebHostOf(request.ApiRoot)}, the GitHub its App belongs to: give the reference api= for the "
                    + "remote's GitHub, or repository=<owner>/<name>.");
            }

            return reference.Trim();
        }

        if (request.Repository is { } named
            && !string.Equals(named.ToString(), repository.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAppException(
                $"the reference {reference} names the repository {named}, but the remote is {repository}; a token for "
                + $"{named} cannot reach it. Leave repository= out, and the remote's own repository is used.");
        }

        return Complete(reference, repository, permissions);
    }

    /// <summary>The GitHub repository <paramref name="remoteUrl"/> names on the GitHub at <paramref name="apiRoot"/>, or null
    /// when it is not an https remote of that GitHub.</summary>
    public static GitHubRepositoryName? RepositoryOn(string apiRoot, string? remoteUrl)
        => Proposals.RemoteUrlParser.TryParseHttpsRepository(remoteUrl, out var host, out var owner, out var repo)
           && GitHubAppSettings.Serves(apiRoot, host)
            ? GitHubRepositoryName.TryParse($"{owner}/{repo}")
            : null;

    /// <summary>What a token of a (completed) reference reaches, in words: its repository or installation, and its permissions.</summary>
    public static string Narrowing(string reference)
    {
        var settings = Settings(LocatorOf(reference), reference);
        var target = settings.TryGetValue("repository", out var repository)
            ? repository
            : settings.TryGetValue("installation", out var installation) ? $"installation {installation}" : "the installation";
        return settings.TryGetValue("permissions", out var granted) ? $"{target} ({granted})" : target;
    }

    /// <summary>
    /// Reads a reference's locator into the token request it names, with what it leaves out taken from the host's
    /// <paramref name="host"/> settings.
    /// </summary>
    /// <exception cref="GitHubAppException">The locator is malformed, or leaves out the App while the host has none,
    /// naming the setting to fix.</exception>
    public static GitHubAppTokenRequest Parse(string locator, GitHubAppSettings host)
        => Parse(locator, host, requireTarget: true);

    /// <summary>
    /// As <see cref="Parse(string, GitHubAppSettings)"/>; with <paramref name="requireTarget"/> false a locator naming
    /// neither an installation nor a repository is accepted, for a caller that completes it (<see cref="Complete"/>).
    /// </summary>
    public static GitHubAppTokenRequest Parse(string locator, GitHubAppSettings host, bool requireTarget)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(host);

        var reference = $"{Prefix}{locator}}}";
        var settings = Settings(locator, reference);

        settings.TryGetValue("app", out var appId);
        settings.TryGetValue("key", out var key);
        GitHubAppIdentity app;
        if (appId is null && key is null)
        {
            app = host.App ?? throw new GitHubAppException(
                $"the reference {reference} names no app and key, and this host has no GitHub App: set "
                + $"{GitHubAppEnvironment.AppIdVariable} and {GitHubAppEnvironment.PrivateKeyVariable} here, or name the App in the "
                + "reference (app=<client ID>;key=keyvault:<vault>/<secret>).");
        }
        else if (appId is null || key is null)
        {
            throw new GitHubAppException(
                $"the reference {reference} names {(appId is null ? "a key but no app" : "an app but no key")}: name both "
                + "(app=<client ID>;key=keyvault:<vault>/<secret>), or neither to use the host's GitHub App.");
        }
        else
        {
            GitHubAppEnvironment.ValidateAppId(appId, $"app in the reference {reference}");
            app = new GitHubAppIdentity(appId, KeyReference(key, reference));
        }

        long? installation = null;
        if (settings.TryGetValue("installation", out var installationText))
        {
            installation = long.TryParse(installationText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : throw new GitHubAppException(
                    $"installation in the reference {reference} must be the installation's numeric id (the number that ends its "
                    + $"settings page's URL); '{installationText}' is not.");
        }

        GitHubRepositoryName? repository = null;
        if (settings.TryGetValue("repository", out var repositoryText))
        {
            repository = GitHubRepositoryName.TryParse(repositoryText) ?? throw new GitHubAppException(
                $"repository in the reference {reference} must be owner/name (like acme/flows); '{repositoryText}' is not.");
        }

        if (requireTarget && installation is null && repository is null)
        {
            throw new GitHubAppException(
                $"the reference {reference} names neither an installation nor a repository: add repository=<owner>/<name> "
                + "(the installation holding it is found) or installation=<id>.");
        }

        var permissions = settings.TryGetValue("permissions", out var permissionsText)
            ? ParsePermissions(permissionsText, reference)
            : new SortedDictionary<string, string>(StringComparer.Ordinal);

        var apiRoot = settings.TryGetValue("api", out var api)
            ? GitHubAppEnvironment.ParseApiRoot(api, $"api in the reference {reference}")
            : host.ApiRoot;

        return new GitHubAppTokenRequest(app, apiRoot, installation, repository, permissions);
    }

    /// <summary>The <c>name=value</c> settings of a locator, refusing a malformed, unknown or repeated one.</summary>
    private static Dictionary<string, string> Settings(string locator, string reference)
    {
        if (locator.Length > MaxLocatorLength)
        {
            throw new GitHubAppException(
                $"the {Scheme} reference is {locator.Length} characters long; a reference names an App, its key and a repository in well under {MaxLocatorLength}.");
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in locator.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new GitHubAppException(
                    $"the reference {reference} has '{trimmed}', which is not name=value; settings are separated by ';' (app=...;key=...;repository=...).");
            }

            var name = trimmed[..equals].Trim().ToLowerInvariant();
            var value = trimmed[(equals + 1)..].Trim();
            if (!Names.Contains(name, StringComparer.Ordinal))
            {
                throw new GitHubAppException(
                    $"the reference {reference} has the unknown setting '{name}'; the settings are {string.Join(", ", Names)}.");
            }

            if (value.Length == 0)
            {
                throw new GitHubAppException($"the reference {reference} gives {name} no value.");
            }

            if (!settings.TryAdd(name, value))
            {
                throw new GitHubAppException($"the reference {reference} names {name} twice; name it once.");
            }
        }

        return settings;
    }

    private static GitHubAppKeySource KeyReference(string key, string reference)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        var scheme = colon > 0 ? key[..colon] : string.Empty;
        if (!SchemePattern().IsMatch(scheme) || colon == key.Length - 1 || key.Contains("${", StringComparison.Ordinal))
        {
            throw new GitHubAppException(
                $"key in the reference {reference} must be a reference to the App's private key written without its ${{ }}, "
                + $"like keyvault:my-vault/github-app-key or env:GITHUB_APP_KEY; '{key}' is not.");
        }

        if (string.Equals(scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAppException(
                $"key in the reference {reference} names another {Scheme} reference; it must name where the private key is kept (keyvault:..., env:...).");
        }

        var keyReference = "${" + key + "}";
        return GitHubAppKeySource.FromReference(keyReference, keyReference);
    }

    private static SortedDictionary<string, string> ParsePermissions(string value, string reference)
    {
        var permissions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            var name = colon > 0 ? item[..colon].Trim().ToLowerInvariant() : string.Empty;
            var level = colon > 0 ? item[(colon + 1)..].Trim().ToLowerInvariant() : string.Empty;
            if (!PermissionPattern().IsMatch(name) || !Levels.Contains(level))
            {
                throw new GitHubAppException(
                    $"permissions in the reference {reference} must be name:level pairs separated by commas, with level read, write "
                    + $"or admin (like contents:read,pull_requests:write); '{item}' is not.");
            }

            if (!permissions.TryAdd(name, level))
            {
                throw new GitHubAppException($"permissions in the reference {reference} name {name} twice; name it once.");
            }
        }

        if (permissions.Count == 0)
        {
            throw new GitHubAppException($"permissions in the reference {reference} names no permission.");
        }

        return permissions;
    }

    [GeneratedRegex(@"^[a-zA-Z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^[a-z][a-z_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();
}
