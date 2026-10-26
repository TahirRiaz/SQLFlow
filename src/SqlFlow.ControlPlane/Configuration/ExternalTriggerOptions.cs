using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Configuration;

/// <summary>
/// External triggers (section <c>ControlPlane:ExternalTriggers</c>): callers outside SQLFlow, a GitHub Actions workflow
/// after a merge or a system holding a key, that sync a repository and fire a selection of its schedules. On by default:
/// nothing is trusted until an administrator creates a trigger or the configuration names one
/// (<see cref="BootstrapOptions.ExternalTriggers"/>), so the default opens nothing.
/// </summary>
public sealed class ExternalTriggerOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionKey = "ControlPlane:ExternalTriggers";

    /// <summary>Whether triggers are accepted and their requests processed. Off, the exchange endpoints and the request
    /// endpoints answer that external triggers are turned off, and requests already made wait until it is on again.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How GitHub's OpenID Connect tokens are checked.</summary>
    public GitHubTriggerOptions GitHub { get; set; } = new();

    /// <summary>How long the credential an exchange issues is valid, in minutes. A caller that waits longer exchanges
    /// again (a GitHub job can ask GitHub for a new token at any time during the job), so this bounds a leaked
    /// credential, not how long a caller may follow its request.</summary>
    public int TokenMinutes { get; set; } = 60;

    /// <summary>How long a request waits for the repository sync that answers it before it fails, in minutes.</summary>
    public int SyncTimeoutMinutes { get; set; } = 30;

    /// <summary>How often the control plane moves its open requests along, in seconds.</summary>
    public int PollSeconds { get; set; } = 2;

    public void Validate()
    {
        if (TokenMinutes is < 5 or > 1440)
        {
            throw new InvalidOperationException($"{SectionKey}:TokenMinutes must be between 5 and 1440.");
        }

        if (SyncTimeoutMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException($"{SectionKey}:SyncTimeoutMinutes must be between 1 and 1440.");
        }

        if (PollSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException($"{SectionKey}:PollSeconds must be between 1 and 60.");
        }

        GitHub.Validate();
    }
}

/// <summary>
/// How the control plane checks a GitHub Actions OpenID Connect token: the issuer it trusts (github.com's, or a GitHub
/// Enterprise Server's), the audience a workflow must request the token for, and the server its run links point at.
/// </summary>
public sealed class GitHubTriggerOptions
{
    /// <summary>The issuer GitHub signs its Actions tokens as; its signing keys are read from the issuer's published
    /// OpenID configuration. Change it only for GitHub Enterprise Server (<c>https://HOST/_services/token</c>).</summary>
    public string Issuer { get; set; } = "https://token.actions.githubusercontent.com";

    /// <summary>The audience a workflow must ask GitHub's token for (the <c>audience</c> of <c>getIDToken</c>). A token for
    /// any other audience is refused, so a token a workflow minted for another service cannot be replayed here. Give each
    /// deployment its own (its address is a good choice) so a token minted for one is refused by the others.</summary>
    public string Audience { get; set; } = "sqlflow";

    /// <summary>The GitHub server the links back to a workflow run point at.</summary>
    public string ServerUrl { get; set; } = "https://github.com";

    /// <summary>Where a workflow finds SQLFlow's sync-and-fire action, as its <c>uses:</c> names it: the workflow the
    /// External triggers page writes out for a trigger uses this. The default is a copy of the action kept in the
    /// workflow's own repository (SQLFlow's <c>integrations/github</c> folder copied to <c>.github/actions/sqlflow</c>),
    /// which needs no access to any other repository; an organization that keeps one copy for every repository names it
    /// here, such as <c>acme/sqlflow-actions/sync-and-fire@v1</c>.</summary>
    public string ActionReference { get; set; } = "./.github/actions/sqlflow";

    public void Validate()
    {
        if (!IsHttpsUrl(Issuer))
        {
            throw new InvalidOperationException($"{ExternalTriggerOptions.SectionKey}:GitHub:Issuer must be an absolute https URL.");
        }

        if (!IsHttpsUrl(ServerUrl))
        {
            throw new InvalidOperationException($"{ExternalTriggerOptions.SectionKey}:GitHub:ServerUrl must be an absolute https URL.");
        }

        if (string.IsNullOrWhiteSpace(ActionReference) || ActionReference.Length > 300 || ActionReference.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{ExternalTriggerOptions.SectionKey}:GitHub:ActionReference must be what a workflow's uses: names, such as ./.github/actions/sqlflow or owner/repo/path@ref.");
        }

        if (string.IsNullOrWhiteSpace(Audience) || Audience.Trim().Length > 256 || Audience.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"{ExternalTriggerOptions.SectionKey}:GitHub:Audience must be set: 1 to 256 characters without spaces, such as the control plane's address.");
        }
    }

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// One trigger of <see cref="BootstrapOptions.ExternalTriggers"/>, the same shape an administrator gives one: the repo
/// source it syncs, the schedules it may fire, and either the GitHub workflow it trusts (<see cref="GitHub"/>) or the key
/// it accepts, as a reference to the secret that holds it (<see cref="KeyReference"/>).
/// </summary>
public sealed class ConfiguredExternalTriggerOptions
{
    /// <summary>What the trigger is for.</summary>
    public string? Description { get; set; }

    /// <summary>The repo source it syncs, by name.</summary>
    public string? RepoSource { get; set; }

    /// <summary>The schedules it may fire, by name.</summary>
    public List<string>? Schedules { get; set; }

    /// <summary>Whether it may fire every schedule of the repo source instead.</summary>
    public bool AllSchedules { get; set; }

    /// <summary>The GitHub workflow it trusts. Set this or <see cref="KeyReference"/>.</summary>
    public ConfiguredGitHubTriggerOptions? GitHub { get; set; }

    /// <summary>The key it accepts, as a secret reference (<c>${keyvault:vault/secret}</c> or <c>${env:NAME}</c>) to a key
    /// of SQLFlow's form (<c>sqlft_</c> and at least 32 random characters), never the key itself. Set this or
    /// <see cref="GitHub"/>.</summary>
    public string? KeyReference { get; set; }
}

/// <summary>The GitHub workflow a configured trigger trusts.</summary>
public sealed class ConfiguredGitHubTriggerOptions
{
    /// <summary>The repository, <c>owner/name</c>.</summary>
    public string? Repository { get; set; }

    /// <summary>The repository's numeric id, to pin it.</summary>
    public string? RepositoryId { get; set; }

    /// <summary>The ref a workflow must run on; <c>*</c> matches any run of characters.</summary>
    public string? Ref { get; set; }

    /// <summary>The workflow file, such as <c>.github/workflows/deliver.yml</c>.</summary>
    public string? Workflow { get; set; }

    /// <summary>The deployment environment the job must run in.</summary>
    public string? Environment { get; set; }
}

/// <summary>A trigger the configuration names, checked: its definition, the setting that names it, and its key's
/// reference for a key trigger (resolved from the vault at start).</summary>
public sealed record ConfiguredExternalTriggerEntry(ExternalTriggerDefinition Definition, string? KeyReference, string Setting);
