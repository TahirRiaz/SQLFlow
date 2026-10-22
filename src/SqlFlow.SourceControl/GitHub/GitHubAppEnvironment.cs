using System.Text.RegularExpressions;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>A GitHub App: the client ID or App ID it signs as, and where its private key is read from.</summary>
/// <param name="AppId">The App's client ID (<c>Iv23li...</c>, which GitHub recommends) or its numeric App ID.</param>
/// <param name="Key">Where the App's private key is read from.</param>
public sealed record GitHubAppIdentity(string AppId, GitHubAppKeySource Key);

/// <summary>
/// The GitHub App a host is configured with, read from its environment (<see cref="GitHubAppEnvironment"/>), and the
/// GitHub API it talks to. <see cref="App"/> is null when the host has no App; the API is always known, so a
/// <c>${githubapp:...}</c> reference naming its own App still knows which GitHub to ask.
/// </summary>
public sealed class GitHubAppSettings
{
    internal GitHubAppSettings(GitHubAppIdentity? app, string apiRoot, string apiSource)
    {
        App = app;
        ApiRoot = apiRoot;
        ApiSource = apiSource;
        WebHost = WebHostOf(apiRoot);
    }

    /// <summary>The host's App, or null when the host has none.</summary>
    public GitHubAppIdentity? App { get; }

    /// <summary>The GitHub REST API root, without a trailing slash: <c>https://api.github.com</c>, or a GitHub Enterprise
    /// Server's <c>https://host/api/v3</c>.</summary>
    public string ApiRoot { get; }

    /// <summary>Where <see cref="ApiRoot"/> came from: the variable that set it, or "the default".</summary>
    public string ApiSource { get; }

    /// <summary>The host whose git remotes the API serves: <c>github.com</c> for <c>api.github.com</c>, <c>acme.ghe.com</c>
    /// for <c>api.acme.ghe.com</c>, and the server itself for a GitHub Enterprise Server API root.</summary>
    public string WebHost { get; }

    /// <summary>Whether a git remote on <paramref name="host"/> is served by this GitHub (and so by its App).</summary>
    public bool Serves(string? host) => Serves(ApiRoot, host);

    /// <summary>The host whose git remotes the API at <paramref name="apiRoot"/> serves (see <see cref="WebHost"/>).</summary>
    public static string WebHostOf(string apiRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiRoot);
        var host = new Uri(apiRoot).Host;
        return host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    /// <summary>Whether a git remote on <paramref name="host"/> is served by the API at <paramref name="apiRoot"/>.</summary>
    public static bool Serves(string apiRoot, string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var webHost = WebHostOf(apiRoot);
        return string.Equals(host, webHost, StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "www." + webHost, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// What a host's environment says about its GitHub App, for a status page: never throws, and never carries the key.
/// </summary>
/// <param name="Configured">Whether the host has a complete, valid App configuration.</param>
/// <param name="AppId">The App's client ID or App ID, when one is set.</param>
/// <param name="KeySource">Where the key is read from (a variable name, a path or a reference), when one is set.</param>
/// <param name="ApiUrl">The GitHub API root the App talks to.</param>
/// <param name="Host">The host whose remotes the App serves.</param>
/// <param name="Problem">Why the configuration cannot be used, or null when it can (or when there is none).</param>
public sealed record GitHubAppStatus(
    bool Configured, string? AppId, string? KeySource, string ApiUrl, string Host, string? Problem);

/// <summary>
/// Reads a host's GitHub App from its environment, the way <c>SQLFLOW_GIT_TOKEN</c> is read: each host (the control
/// plane, every worker) sets the same variables, mints its own short-lived installation tokens, and no token ever
/// travels between them.
/// <list type="bullet">
/// <item><c>SQLFLOW_GITHUB_APP_ID</c>: the App's client ID (recommended by GitHub) or App ID.</item>
/// <item><c>SQLFLOW_GITHUB_APP_PRIVATE_KEY</c>: the private key as PEM text, the base64 of the PEM, or a
/// <c>${keyvault:vault/secret}</c> (or other) reference to either; or instead</item>
/// <item><c>SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH</c>: the absolute path of the App's .pem file.</item>
/// <item><c>SQLFLOW_GITHUB_APP_API_URL</c> (optional): the API root of GitHub Enterprise Server or GHE.com;
/// <c>https://api.github.com</c> when unset.</item>
/// </list>
/// A partial configuration (an App ID without a key, a key without an App ID, or both key variables) is refused with
/// the variable to fix, never ignored: a host that silently fell back to no credential would fail later, and further
/// from the cause.
/// </summary>
public static partial class GitHubAppEnvironment
{
    public const string AppIdVariable = "SQLFLOW_GITHUB_APP_ID";
    public const string PrivateKeyVariable = "SQLFLOW_GITHUB_APP_PRIVATE_KEY";
    public const string PrivateKeyPathVariable = "SQLFLOW_GITHUB_APP_PRIVATE_KEY_PATH";
    public const string ApiUrlVariable = "SQLFLOW_GITHUB_APP_API_URL";

    /// <summary>The public GitHub API.</summary>
    public const string DefaultApiRoot = "https://api.github.com";

    /// <summary>Reads the process environment; see <see cref="Read(Func{string, string?})"/>.</summary>
    public static GitHubAppSettings Read() => Read(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads the App the variables <paramref name="environment"/> answers describe, validating every value. A host with
    /// none of the App variables has no App (<see cref="GitHubAppSettings.App"/> is null).
    /// </summary>
    /// <exception cref="GitHubAppException">The variables describe an incomplete or malformed App, naming the variable to fix.</exception>
    public static GitHubAppSettings Read(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var appId = Value(environment, AppIdVariable);
        var key = Value(environment, PrivateKeyVariable);
        var keyPath = Value(environment, PrivateKeyPathVariable);
        var apiUrl = Value(environment, ApiUrlVariable);

        var apiRoot = apiUrl is null ? DefaultApiRoot : ParseApiRoot(apiUrl, ApiUrlVariable);
        var apiSource = apiUrl is null ? "the default" : ApiUrlVariable;

        if (appId is null)
        {
            if (key is not null || keyPath is not null)
            {
                throw new GitHubAppException(
                    $"{(key is not null ? PrivateKeyVariable : PrivateKeyPathVariable)} is set but {AppIdVariable} is not: "
                    + $"set {AppIdVariable} to the GitHub App's client ID, or remove the key variable.");
            }

            return new GitHubAppSettings(null, apiRoot, apiSource);
        }

        ValidateAppId(appId, AppIdVariable);
        if (key is not null && keyPath is not null)
        {
            throw new GitHubAppException(
                $"both {PrivateKeyVariable} and {PrivateKeyPathVariable} are set; set only one of them, so it is clear which key the GitHub App signs with.");
        }

        if (key is null && keyPath is null)
        {
            throw new GitHubAppException(
                $"{AppIdVariable} is set but the App has no private key: set {PrivateKeyVariable} (the PEM, its base64, or a "
                + $"${{keyvault:vault/secret}} reference) or {PrivateKeyPathVariable} (the .pem file's path).");
        }

        var source = key is not null
            ? GitHubAppKeySource.FromVariable(PrivateKeyVariable, key)
            : GitHubAppKeySource.FromFile(PrivateKeyPathVariable, keyPath!);
        return new GitHubAppSettings(new GitHubAppIdentity(appId, source), apiRoot, apiSource);
    }

    /// <summary>What the process environment says about its App; see <see cref="Describe(Func{string, string?})"/>.</summary>
    public static GitHubAppStatus Describe() => Describe(Environment.GetEnvironmentVariable);

    /// <summary>What <paramref name="environment"/> says about the host's App, for a status page. Never throws: a
    /// configuration that cannot be used is reported through <see cref="GitHubAppStatus.Problem"/>.</summary>
    public static GitHubAppStatus Describe(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            var settings = Read(environment);
            return new GitHubAppStatus(
                settings.App is not null, settings.App?.AppId, settings.App?.Key.Description, settings.ApiRoot, settings.WebHost, null);
        }
        catch (GitHubAppException ex)
        {
            var apiUrl = Value(environment, ApiUrlVariable);
            string apiRoot;
            try
            {
                apiRoot = apiUrl is null ? DefaultApiRoot : ParseApiRoot(apiUrl, ApiUrlVariable);
            }
            catch (GitHubAppException)
            {
                apiRoot = apiUrl!;
            }

            var host = Uri.TryCreate(apiRoot, UriKind.Absolute, out var uri)
                ? (uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host)
                : string.Empty;
            var keySource = Value(environment, PrivateKeyPathVariable) is { } path
                ? $"{PrivateKeyPathVariable} ({path})"
                : Value(environment, PrivateKeyVariable) is not null ? PrivateKeyVariable : null;
            return new GitHubAppStatus(false, Value(environment, AppIdVariable), keySource, apiRoot, host, ex.Message);
        }
    }

    /// <summary>
    /// Checks an App's client ID or App ID: letters, digits, dots, dashes and underscores, at most 100 characters. Both
    /// forms GitHub issues (<c>Iv23liAbCd...</c>, <c>Iv1.0123abcd...</c>, <c>123456</c>) pass.
    /// </summary>
    /// <exception cref="GitHubAppException">The value is not an App ID, naming <paramref name="where"/>.</exception>
    internal static void ValidateAppId(string appId, string where)
    {
        if (!AppIdPattern().IsMatch(appId))
        {
            throw new GitHubAppException(
                $"{where} must be the GitHub App's client ID (like Iv23liAbCdEf) or its numeric App ID; '{appId}' is neither.");
        }
    }

    /// <summary>
    /// The API root <paramref name="value"/> names, without a trailing slash: an absolute <c>https</c> URL with no user,
    /// query or fragment, because the App's signed token is sent to it.
    /// </summary>
    /// <exception cref="GitHubAppException">The value is not such a URL, naming <paramref name="where"/>.</exception>
    internal static string ParseApiRoot(string value, string where)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            throw new GitHubAppException($"{where} must be the absolute URL of a GitHub API root (like https://api.github.com); '{value}' is not a URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitHubAppException($"{where} must be an https URL, because the GitHub App's signed token is sent to it; '{value}' is not.");
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new GitHubAppException($"{where} must be a plain API root with no user, query or fragment (like https://ghe.example.com/api/v3); '{value}' has one.");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string? Value(Func<string, string?> environment, string name)
        => environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();
}
