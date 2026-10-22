namespace SqlFlow.SourceControl.Proposals;

/// <summary>The git hosts SQLFlow can open a pull request against. The set is deliberately small: it matches the
/// two remotes the engine already speaks git to over HTTPS (a GitHub PAT or a BitBucket app password), so a
/// proposal never pretends to support a host whose pull-request API is not implemented.</summary>
public enum PullRequestProvider
{
    GitHub,
    Bitbucket,
}

/// <summary>A remote resolved to the coordinates a pull-request API needs: which provider serves it, and the
/// owner/workspace and repository slug parsed out of the clone URL.</summary>
public sealed record PullRequestCoordinates(PullRequestProvider Provider, string Host, string Owner, string Repo);

/// <summary>The request to open a pull request: the resolved coordinates come separately; this carries the branches,
/// the human-facing title/body, and the same credential used to push (a GitHub PAT, or a BitBucket username + app
/// password). The secret lives in memory only for the call and is never logged.</summary>
public sealed record PullRequestRequest(
    string BaseBranch, string HeadBranch, string Title, string? Body, string? Username, string Secret);

/// <summary>An opened pull request: the browser URL a human reviews it at, and its provider-assigned number.</summary>
public sealed record PullRequestResult(string Url, int Number);

/// <summary>Opens a pull request on one git host. Registered per <see cref="Provider"/>; the caller selects the
/// implementation matching the remote's resolved coordinates. Implementations translate one provider's HTTP API and
/// map any non-success response onto a <see cref="SqlFlow.Core.SqlFlowException"/> with a secret-redacted message.</summary>
public interface IPullRequestPublisher
{
    /// <summary>The host this implementation opens pull requests on.</summary>
    PullRequestProvider Provider { get; }

    /// <summary>Opens the pull request and returns its URL and number.</summary>
    Task<PullRequestResult> CreateAsync(PullRequestCoordinates coordinates, PullRequestRequest request, CancellationToken ct = default);
}

/// <summary>Resolves a git clone URL to the coordinates a pull-request API needs. HTTPS only (the same transport the
/// engine clones and pushes over), and only the two supported hosts; anything else returns null so the caller can
/// refuse cleanly rather than guess an unsupported API shape.</summary>
public static class RemoteUrlParser
{
    /// <summary>Parses <paramref name="remoteUrl"/> into pull-request coordinates, or returns null when the URL is
    /// not an <c>https</c> clone URL of a supported host with a <c>{owner}/{repo}</c> path.</summary>
    public static PullRequestCoordinates? Parse(string? remoteUrl)
    {
        if (!TryParseHttpsRepository(remoteUrl, out var host, out var owner, out var repo))
        {
            return null;
        }

        var provider = host switch
        {
            "github.com" or "www.github.com" => PullRequestProvider.GitHub,
            "bitbucket.org" or "www.bitbucket.org" => PullRequestProvider.Bitbucket,
            _ => (PullRequestProvider?)null,
        };
        return provider is null ? null : new PullRequestCoordinates(provider.Value, host, owner, repo);
    }

    /// <summary>
    /// Parses an <c>https</c> clone URL of any host into its lower-cased host and the <c>{owner}/{repo}</c> its path
    /// starts with (a trailing <c>.git</c> dropped); false for anything else. The one parse both the pull-request
    /// coordinates and a GitHub App's repository are read with.
    /// </summary>
    public static bool TryParseHttpsRepository(
        string? remoteUrl,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? host,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? owner,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? repo)
    {
        host = owner = repo = null;
        if (string.IsNullOrWhiteSpace(remoteUrl) || !Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        // HTTPS only: the whole source-control path is HTTPS (LibGit2Sharp clone/push, and the provider REST APIs),
        // so an ssh:// or git@ remote has no supported push-and-open-PR flow here.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        host = uri.Host.ToLowerInvariant();
        owner = segments[0];
        repo = name;
        return true;
    }
}
