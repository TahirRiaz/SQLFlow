using LibGit2Sharp;

namespace SQLFlowCore.Services.Git
{
    /// <summary>
    /// Provides an interface for Git credential providers.
    /// </summary>
    internal interface IGitCredentialsProvider
    {
        /// <summary>
        /// Gets the credentials for a given URL and username.
        /// </summary>
        /// <param name="url">The URL for which to get the credentials.</param>
        /// <param name="usernameFromUrl">The username from the URL.</param>
        /// <param name="types">The supported credential types.</param>
        /// <returns>The credentials for the given URL and username.</returns>
        Credentials GetCredentials(string url, string usernameFromUrl, SupportedCredentialTypes types);
    }
}