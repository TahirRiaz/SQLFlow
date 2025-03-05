using System;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using System.Text.RegularExpressions;
using System.IO;
using Azure.Security.KeyVault.Certificates;
using SQLFlowCore.Services.CloudResources;

namespace SQLFlowCore.Services.AzureResources
{
    /// <summary>
    /// Manages interactions with Azure Key Vault.
    /// </summary>
    public class AzureKeyVaultManager
    {
        private readonly ClientSecretCredential clientSecretCredential;
        private readonly SecretClient secretClient;
        private readonly CertificateClient certificateClient;
        private readonly string keyVaultName;
        private readonly string keyVaultUrl;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureKeyVaultManager"/> class using the DefaultAzureCredential.
        /// </summary>
        /// <param name="KeyVaultName">The name of the Azure Key Vault.</param>
        public AzureKeyVaultManager(string KeyVaultName)
        {
            DefaultAzureCredentialOptions credentialOptions = CloudEnvironmentDetector.GetDefaultAzureCredentialOptions();
            var credential = new DefaultAzureCredential(credentialOptions);
            keyVaultName = KeyVaultName;
            keyVaultUrl = @$"https://{KeyVaultName}.vault.azure.net/";
            secretClient = new SecretClient(new Uri(keyVaultUrl), credential);
            certificateClient = new CertificateClient(vaultUri: new Uri(keyVaultUrl), credential: credential);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureKeyVaultManager"/> class using a ClientSecretCredential.
        /// </summary>
        /// <param name="TenantId">The tenant ID.</param>
        /// <param name="ClientId">The client ID.</param>
        /// <param name="ClientSecret">The client secret.</param>
        /// <param name="KeyVaultName">The name of the Azure Key Vault.</param>
        public AzureKeyVaultManager(string TenantId, string ClientId, string ClientSecret, string KeyVaultName)
        {
            keyVaultName = KeyVaultName;
            keyVaultUrl = @$"https://{KeyVaultName}.vault.azure.net/";
            if (!string.IsNullOrEmpty(ClientSecret) && !string.IsNullOrEmpty(ClientId))
            {
                clientSecretCredential = new ClientSecretCredential(TenantId, ClientId, ClientSecret);
                secretClient = new SecretClient(new Uri(keyVaultUrl), clientSecretCredential);
                certificateClient = new CertificateClient(vaultUri: new Uri(keyVaultUrl), credential: clientSecretCredential);
            }
            else
            {
                DefaultAzureCredentialOptions credentialOptions = CloudEnvironmentDetector.GetDefaultAzureCredentialOptions();
                var credential = new DefaultAzureCredential(credentialOptions);
                secretClient = new SecretClient(new Uri(keyVaultUrl), credential);
                certificateClient = new CertificateClient(vaultUri: new Uri(keyVaultUrl), credential: credential);
            }
        }

        /// <summary>
        /// Creates a new secret in the Azure Key Vault.
        /// </summary>
        /// <param name="SecretName">The name of the secret.</param>
        /// <param name="SecretValue">The value of the secret.</param>
        /// <returns>The created KeyVaultSecret.</returns>
        public async Task<KeyVaultSecret> CreateSecretAsync(string SecretName, string SecretValue)
        {
            KeyVaultSecret newSecret = null;
            try
            {
                // Create a new secret
                newSecret = await secretClient.SetSecretAsync(SecretName, SecretValue);
            }
            catch (Exception)
            {
                throw;
            }

            return newSecret;
        }

        /// <summary>
        /// Retrieves a secret from the Azure Key Vault.
        /// </summary>
        /// <param name="secretName">The name of the secret.</param>
        /// <returns>The value of the secret.</returns>
        public string GetSecret(string secretName)
        {
            string rValue = "";

            if (keyVaultName.Length > 0 && secretName.Length > 0)
            {
                rValue = secretClient.GetSecret(secretName).Value.Value;
            }

            return rValue;
        }
        /// <summary>
        /// Writes a certificate to a temporary file.
        /// </summary>
        /// <param name="certificateName">The name of the certificate.</param>
        /// <returns>The full path to the temporary file.</returns>
        public async Task<string> WriteCertificateToTempFile(string certificateName)
        {
            try
            {
                // Generate a unique file name to avoid conflicts
                string fileName = $"{certificateName}.pfx";

                // Get the path to the temporary folder
                //@"C:\local\"
                string tempPath = Path.GetTempPath();

                // Combine the temporary folder path with the unique file name
                string fullPath = Path.Combine(tempPath, fileName);

                if (keyVaultName.Length > 0 && certificateName.Length > 0)
                {
                    byte[] pfxBytes = await DownloadCertificateAsync(certificateName);
                    //
                    await File.WriteAllBytesAsync(fullPath, pfxBytes);
                }

                return fullPath;
            }
            catch (Exception ex)
            {
                // Handle or propagate the exception as needed
                throw new InvalidOperationException("Error writing certificate to temp file", ex);
            }
        }

        /// <summary>
        /// Downloads a certificate from the Azure Key Vault.
        /// </summary>
        /// <param name="certificateName">The name of the certificate.</param>
        /// <returns>The certificate data in PFX format.</returns>
        private async Task<byte[]> DownloadCertificateAsync(string certificateName)
        {
            try
            {
                // Get the certificate with the private key
                KeyVaultCertificateWithPolicy certificate = await certificateClient.GetCertificateAsync(certificateName);

                // Convert the certificate to a byte array in PFX format
                // Note: You might need to extract the private key depending on your scenario
                byte[] pfxData = certificate.Cer;

                return pfxData;
            }
            catch (Exception ex)
            {
                // Handle or propagate the exception as needed
                throw new InvalidOperationException("Error getting the certificate from keyvault", ex);
            }
        }

        /// <summary>
        /// Makes a valid secret name by replacing invalid characters and ensuring the name is within length constraints.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>A valid secret name.</returns>
        public static string MakeValidSecretName(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                throw new ArgumentException("Input cannot be null or empty", nameof(input));
            }

            // Replace invalid characters with a dash
            string validName = Regex.Replace(input, "[^a-zA-Z0-9-]", "-");

            // Remove leading, trailing, and consecutive dashes
            validName = Regex.Replace(validName, "-{2,}", "-").Trim('-');

            // Ensure the name is within the length constraints
            if (validName.Length > 127)
            {
                validName = validName.Substring(0, 127);
            }
            else if (validName.Length == 0)
            {
                throw new ArgumentException("Input does not contain valid characters for a secret name.");
            }

            return validName;
        }

    }

}
