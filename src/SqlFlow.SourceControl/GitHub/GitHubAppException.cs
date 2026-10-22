using SqlFlow.Core;

namespace SqlFlow.SourceControl.GitHub;

/// <summary>
/// A GitHub App could not be used: its configuration is incomplete or malformed, its private key could not be read, or
/// GitHub refused or did not answer a request made as the App. The message names the App, what was asked for and what
/// to change, and never carries the private key, a JSON web token or an installation token.
/// </summary>
public class GitHubAppException : SqlFlowException
{
    public GitHubAppException(string message)
        : base(message)
    {
    }

    public GitHubAppException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">The failure, secret-free.</param>
    /// <param name="statusCode">The HTTP status GitHub answered with, when the failure is GitHub's answer.</param>
    public GitHubAppException(string message, int? statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status GitHub answered with, or null when the failure happened before or without an answer.</summary>
    public int? StatusCode { get; }
}

/// <summary>
/// The App has no installation that reaches the repository asked for: it is not installed on the repository's owner,
/// the installation's repository selection leaves the repository out, or the repository does not exist. A caller that
/// can work without the App (a public remote, or a host token) uses this to fall back; every other failure is final.
/// </summary>
public sealed class GitHubAppNotInstalledException : GitHubAppException
{
    public GitHubAppNotInstalledException(string message, string repository, int? statusCode)
        : base(message, statusCode)
    {
        Repository = repository;
    }

    /// <summary>The repository the App does not reach, as <c>owner/name</c>.</summary>
    public string Repository { get; }
}
