using LibGit2Sharp;
using SharpBucket.Authentication;

namespace SQLFlowCore.Services.Git
{
    /// <summary>
    /// Provides Git credentials using OAuth2.
    /// </summary>
    internal class OAuth2GitCredentialsProvider : IGitCredentialsProvider
    {
        private OAuth2TokenProvider TokenProvider { get; }

        private UsernamePasswordCredentials OAuth2TokenCredentials { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OAuth2GitCredentialsProvider"/> class.
        /// </summary>
        /// <param name="consumerKey">The consumer key.</param>
        /// <param name="consumerSecret">The consumer secret.</param>
        internal OAuth2GitCredentialsProvider(string consumerKey, string consumerSecret)
        {
            TokenProvider = new OAuth2TokenProvider(consumerKey, consumerSecret);
        }

        /// <summary>
        /// Gets the credentials for a given URL and username.
        /// </summary>
        /// <param name="url">The URL for which to get the credentials.</param>
        /// <param name="usernameFromUrl">The username from the URL.</param>
        /// <param name="types">The supported credential types.</param>
        /// <returns>The credentials for the given URL and username.</returns>
        public Credentials GetCredentials(string url, string usernameFromUrl, SupportedCredentialTypes types)
        {
            if (OAuth2TokenCredentials == null)
            {
                // TODO the token should be kept somewhere to implement refresh token scenario one day.
                var token = TokenProvider.GetClientCredentialsToken();
                OAuth2TokenCredentials = new UsernamePasswordCredentials
                {
                    Username = "x-token-auth",
                    Password = token.AccessToken
                };
            }

            return OAuth2TokenCredentials;
        }
    }
}
