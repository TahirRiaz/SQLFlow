using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Who a GitHub Actions job is, as the OpenID Connect token GitHub minted for it states: the repository (and its numeric
/// id, owner and owner id), the ref and commit the workflow runs on, the workflow file (<see cref="WorkflowRef"/>), the
/// deployment environment, the event, the run, its attempt and the actor that started it. GitHub signs every claim, so a
/// workflow cannot state any of them falsely.
/// </summary>
public sealed record GitHubActionsIdentity(
    string Repository, string? RepositoryId, string? RepositoryOwner, string? RepositoryOwnerId, string Ref, string? Sha,
    string? WorkflowRef, string? JobWorkflowRef, string? Environment, string? EventName, string? RunId, string? RunAttempt,
    string? Actor, string? Subject)
{
    /// <summary>The workflow file's path in the repository, read from <see cref="WorkflowRef"/>
    /// (<c>owner/repo/.github/workflows/deliver.yml@refs/heads/main</c> gives <c>.github/workflows/deliver.yml</c>); null
    /// when the token states none.</summary>
    public string? WorkflowPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkflowRef))
            {
                return null;
            }

            var at = WorkflowRef.LastIndexOf('@');
            var path = at < 0 ? WorkflowRef : WorkflowRef[..at];
            var prefix = Repository + "/";
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
        }
    }

    /// <summary>One line naming the job, for a request's record and a trigger's "last used": repository, workflow, ref,
    /// commit, run, attempt, actor and event, each only when the token states it.</summary>
    public string Describe()
    {
        var parts = new List<string>
        {
            "GitHub " + Repository + (RepositoryId is null ? string.Empty : $" (id {RepositoryId})"),
        };
        if (WorkflowPath is { } workflow)
        {
            parts.Add("workflow " + workflow);
        }

        parts.Add(Ref + (Sha is { Length: >= 7 } sha ? " at " + sha[..7] : string.Empty));
        if (RunId is not null)
        {
            parts.Add("run " + RunId + (RunAttempt is null ? string.Empty : " attempt " + RunAttempt));
        }

        if (Environment is not null)
        {
            parts.Add("environment " + Environment);
        }

        if (Actor is not null)
        {
            parts.Add("by " + Actor + (EventName is null ? string.Empty : $" ({EventName})"));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The link to this job's workflow run attempt on <paramref name="serverUrl"/>, or null when the token names
    /// no run.</summary>
    public string? RunUrl(string serverUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        if (RunId is null || !RunId.All(char.IsAsciiDigit))
        {
            return null;
        }

        var url = $"{serverUrl.TrimEnd('/')}/{Repository}/actions/runs/{RunId}";
        return RunAttempt is { } attempt && attempt.All(char.IsAsciiDigit) ? $"{url}/attempts/{attempt}" : url;
    }
}

/// <summary>The outcome of checking a GitHub token: the job's identity, or why the token is refused; <see cref="Outage"/>
/// when GitHub's signing keys could not be read, which is worth retrying, unlike a bad token.</summary>
public sealed record GitHubTokenValidation(GitHubActionsIdentity? Identity, string? Problem, bool Outage = false)
{
    public static GitHubTokenValidation Refused(string problem) => new(null, problem);
}

/// <summary>Checks a GitHub Actions OpenID Connect token. Behind an interface so the exchange is testable without GitHub.</summary>
public interface IGitHubActionsTokenValidator
{
    /// <summary>The token's identity, or why it is refused. Never throws for an untrusted token.</summary>
    Task<GitHubTokenValidation> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>
/// Validates GitHub Actions OpenID Connect tokens against the configured issuer's published signing keys: the issuer must
/// be the configured one (github.com's, or a GitHub Enterprise Server's), the audience the configured one (so a token a
/// workflow minted for another service is refused), the signature RS256 under one of the issuer's keys (refreshed once on
/// a key the cached set does not hold, for rollover), and the lifetime current within 30 seconds. A metadata outage fails
/// closed and is reported as one, never answered from stale or no keys.
/// </summary>
public sealed class GitHubActionsTokenValidator : IGitHubActionsTokenValidator
{
    private readonly GitHubTriggerOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly ILogger<GitHubActionsTokenValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public GitHubActionsTokenValidator(IOptions<ControlPlaneOptions> options, ILogger<GitHubActionsTokenValidator> logger)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).Value.ExternalTriggers.GitHub,
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{options.Value.ExternalTriggers.GitHub.Issuer.TrimEnd('/')}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever()),
            logger)
    {
    }

    /// <summary>A validator reading the issuer's metadata from <paramref name="metadata"/>: the seam tests use to sign
    /// tokens with keys of their own.</summary>
    public GitHubActionsTokenValidator(
        GitHubTriggerOptions options, IConfigurationManager<OpenIdConnectConfiguration> metadata, ILogger<GitHubActionsTokenValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _metadata = metadata;
        _logger = logger;
    }

    public async Task<GitHubTokenValidation> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return GitHubTokenValidation.Refused("No GitHub token was presented.");
        }

        JsonWebToken unvalidated;
        try
        {
            unvalidated = _handler.ReadJsonWebToken(token.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return GitHubTokenValidation.Refused("The GitHub token is not a JSON web token.");
        }

        // Named before any key is fetched, so a token from another issuer (an Entra token, a SQLFlow token) is told
        // exactly what it is instead of failing on a signature.
        var issuer = _options.Issuer.TrimEnd('/');
        if (!string.Equals(unvalidated.Issuer?.TrimEnd('/'), issuer, StringComparison.Ordinal))
        {
            return GitHubTokenValidation.Refused(
                $"The token was issued by '{unvalidated.Issuer}', not by GitHub Actions ('{issuer}'). Present the token the job gets from GitHub (the id-token permission and getIDToken).");
        }

        var audiences = unvalidated.Audiences.ToList();
        if (!audiences.Contains(_options.Audience, StringComparer.Ordinal))
        {
            return GitHubTokenValidation.Refused(
                $"The GitHub token is for audience '{string.Join("', '", audiences)}'. Ask GitHub for a token with audience '{_options.Audience}' (the audience input of the action, or getIDToken('{_options.Audience}')).");
        }

        OpenIdConnectConfiguration metadata;
        try
        {
            metadata = await _metadata.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "GitHub's OpenID configuration could not be read from {Issuer}.", issuer);
            return new GitHubTokenValidation(null, $"GitHub's signing keys could not be read from {issuer}; try again shortly.", Outage: true);
        }

        var result = await ValidateOnceAsync(token.Trim(), metadata, issuer).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Key rollover: GitHub published a key after the cached set was read. Refresh once; a key still unknown is an
            // untrusted token.
            _metadata.RequestRefresh();
            try
            {
                metadata = await _metadata.GetConfigurationAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "GitHub's OpenID configuration could not be refreshed from {Issuer} after an unknown signing key.", issuer);
                return new GitHubTokenValidation(null, $"GitHub's signing keys could not be read from {issuer}; try again shortly.", Outage: true);
            }

            result = await ValidateOnceAsync(token.Trim(), metadata, issuer).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            var reason = result.Exception switch
            {
                SecurityTokenExpiredException => "The GitHub token has expired; ask GitHub for a new one.",
                SecurityTokenNotYetValidException => "The GitHub token is not valid yet; check the clock of the machine presenting it.",
                SecurityTokenSignatureKeyNotFoundException or SecurityTokenInvalidSignatureException
                    => "The GitHub token's signature does not verify against GitHub's published keys.",
                _ => "The GitHub token is not valid.",
            };
            _logger.LogWarning("GitHub token refused: {Reason} ({Exception})", reason, result.Exception?.GetType().Name ?? "invalid");
            return GitHubTokenValidation.Refused(reason);
        }

        var claims = result.Claims;
        var repository = Claim(claims, "repository");
        var gitRef = Claim(claims, "ref");
        if (repository is null || gitRef is null)
        {
            return GitHubTokenValidation.Refused("The GitHub token states no repository or ref, so no trigger can trust it.");
        }

        return new GitHubTokenValidation(
            new GitHubActionsIdentity(
                repository, Claim(claims, "repository_id"), Claim(claims, "repository_owner"), Claim(claims, "repository_owner_id"),
                gitRef, Claim(claims, "sha"), Claim(claims, "workflow_ref"), Claim(claims, "job_workflow_ref"),
                Claim(claims, "environment"), Claim(claims, "event_name"), Claim(claims, "run_id"), Claim(claims, "run_attempt"),
                Claim(claims, "actor"), Claim(claims, "sub")),
            null);
    }

    private async Task<TokenValidationResult> ValidateOnceAsync(string token, OpenIdConnectConfiguration metadata, string issuer)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [issuer, issuer + "/"],
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = metadata.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        return await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
    }

    /// <summary>A claim's value as a string: GitHub sends ids as strings, but a number is read the same way.</summary>
    private static string? Claim(IDictionary<string, object> claims, string name)
        => claims.TryGetValue(name, out var value)
            ? value switch
            {
                string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                long l => l.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                _ => null,
            }
            : null;
}

/// <summary>
/// Which external trigger a GitHub job may act as: the enabled GitHub triggers whose repository, repository id, ref,
/// workflow and environment the job's token states. A caller may name the trigger (when several trust the same workflow);
/// otherwise exactly one must match. Every refusal says why, naming the triggers of the job's repository and what each
/// asks for, so a workflow author can fix the workflow or an administrator the trigger from the job's log alone.
/// </summary>
public static class GitHubTriggerMatch
{
    /// <summary>The outcome of choosing: the trigger, or the HTTP status and reason a refusal answers with.</summary>
    public sealed record Choice(CatalogExternalTrigger? Trigger, int Status, string? Problem);

    /// <summary>Chooses the trigger <paramref name="identity"/> acts as among <paramref name="triggers"/> (the enabled GitHub
    /// triggers), honouring <paramref name="requestedName"/> when the caller named one.</summary>
    public static Choice Choose(IReadOnlyList<CatalogExternalTrigger> triggers, GitHubActionsIdentity identity, string? requestedName)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(identity);
        var forRepository = triggers
            .Where(t => string.Equals(t.GitHubRepository, identity.Repository, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var name = string.IsNullOrWhiteSpace(requestedName) ? null : requestedName.Trim();
        if (name is not null)
        {
            var named = forRepository.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (named is null)
            {
                return new Choice(null, StatusCodes.Status403Forbidden,
                    $"No enabled trigger named '{name}' trusts {identity.Repository}. {Known(forRepository, identity)}");
            }

            return Mismatch(named, identity) is { } reason
                ? new Choice(null, StatusCodes.Status403Forbidden, $"Trigger '{named.Name}' does not trust this job: {reason}.")
                : new Choice(named, StatusCodes.Status200OK, null);
        }

        var matching = forRepository.Where(t => Mismatch(t, identity) is null).ToList();
        return matching.Count switch
        {
            1 => new Choice(matching[0], StatusCodes.Status200OK, null),
            0 => new Choice(null, StatusCodes.Status403Forbidden,
                $"No enabled trigger trusts this job ({identity.Describe()}). {Known(forRepository, identity)}"),
            _ => new Choice(null, StatusCodes.Status409Conflict,
                $"Several triggers trust this job: {string.Join(", ", matching.Select(t => t.Name))}. Name the one to act as."),
        };
    }

    /// <summary>Why <paramref name="trigger"/> does not trust <paramref name="identity"/>, or null when it does.</summary>
    public static string? Mismatch(CatalogExternalTrigger trigger, GitHubActionsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(trigger.GitHubRepository, identity.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return $"it trusts {trigger.GitHubRepository}, and the job runs in {identity.Repository}";
        }

        if (trigger.GitHubRepositoryId is { } pinned && !string.Equals(pinned, identity.RepositoryId, StringComparison.Ordinal))
        {
            return $"it is pinned to repository id {pinned}, and the job's repository has id {identity.RepositoryId ?? "(none stated)"}";
        }

        if (!GlobMatches(trigger.GitHubRef ?? string.Empty, identity.Ref))
        {
            return $"it trusts ref {trigger.GitHubRef}, and the job runs on {identity.Ref}";
        }

        if (trigger.GitHubWorkflow is { } workflow && !string.Equals(workflow, identity.WorkflowPath, StringComparison.Ordinal))
        {
            return $"it trusts workflow {workflow}, and the job runs {identity.WorkflowPath ?? "(no workflow stated)"}";
        }

        if (trigger.GitHubEnvironment is { } environment && !string.Equals(environment, identity.Environment, StringComparison.Ordinal))
        {
            return identity.Environment is null
                ? $"it asks for environment {environment}, and the job runs in none (set 'environment: {environment}' on the job)"
                : $"it asks for environment {environment}, and the job runs in {identity.Environment}";
        }

        return null;
    }

    /// <summary>Whether <paramref name="value"/> matches <paramref name="pattern"/>, where <c>*</c> matches any run of
    /// characters (none included) and everything else matches itself, case-sensitively as git refs compare.</summary>
    public static bool GlobMatches(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);
        if (pattern.Length == 0)
        {
            return false;
        }

        // Iterative wildcard match with backtracking to the last star: linear in practice, no regex to compile.
        int p = 0, v = 0, star = -1, mark = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static string Known(IReadOnlyList<CatalogExternalTrigger> forRepository, GitHubActionsIdentity identity)
        => forRepository.Count == 0
            ? $"No enabled trigger trusts the repository {identity.Repository}; an administrator creates one on the External triggers page."
            : "The triggers for this repository: " + string.Join("; ", forRepository.Select(t => $"'{t.Name}' ({Mismatch(t, identity) ?? "trusts it"})")) + ".";
}
