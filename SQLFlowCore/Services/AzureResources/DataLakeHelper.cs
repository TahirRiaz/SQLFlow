using Azure.Identity;
using Azure.Storage.Files.DataLake;
using SQLFlowCore.Services.CloudResources;
using System;

namespace SQLFlowCore.Services.AzureResources
{
    /// <summary>
    /// Provides helper methods for interacting with Azure Data Lake.
    /// </summary>
    public class DataLakeHelper
    {
        /// <summary>
        /// Gets a DataLakeFileSystemClient using the provided parameters.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="applicationId">The application ID.</param>
        /// <param name="clientSecret">The client secret.</param>
        /// <param name="keyVaultName">The name of the Key Vault.</param>
        /// <param name="secretName">The name of the secret.</param>
        /// <param name="accountName">The account name.</param>
        /// <param name="blobContainer">The blob container.</param>
        /// <returns>A DataLakeFileSystemClient.</returns>
        public static DataLakeFileSystemClient GetDataLakeFileSystemClient(
        string tenantId,
        string applicationId,
        string clientSecret,
        string keyVaultName,
        string secretName,
        string accountName,
        string blobContainer)
        {
            // Create shared key credential
            // StorageSharedKeyCredential sharedKeyCredential = new StorageSharedKeyCredential(accountName, accountKey);
            //if (keyVaultName.Length > 0 && secretName.Length > 0)
            //{
            //    AzureKeyVaultManager keyVaultManager = new AzureKeyVaultManager(keyVaultName);
            //    clientSecret = keyVaultManager.GetSecret(secretName);
            //}

            if (!string.IsNullOrEmpty(clientSecret) && !string.IsNullOrEmpty(applicationId))
            {
                var clientSecretCredential = new ClientSecretCredential(tenantId, applicationId, clientSecret);

                // Construct Data Lake Service Client URI
                string dfsUri = $"https://{accountName}.dfs.core.windows.net";

                // Initialize Data Lake Service Client
                DataLakeServiceClient dataLakeServiceClient = new DataLakeServiceClient(new Uri(dfsUri), clientSecretCredential);

                // Get and return the FileSystemClient for the specified blobContainer
                return dataLakeServiceClient.GetFileSystemClient(blobContainer);

            }
            else
            {
                DefaultAzureCredentialOptions credentialOptions = CloudEnvironmentDetector.GetDefaultAzureCredentialOptions();
                var credential = new DefaultAzureCredential(credentialOptions);

                // Construct Data Lake Service Client URI
                string dfsUri = $"https://{accountName}.dfs.core.windows.net";

                // Initialize Data Lake Service Client
                DataLakeServiceClient dataLakeServiceClient = new DataLakeServiceClient(new Uri(dfsUri), credential);



                // Get and return the FileSystemClient for the specified blobContainer
                return dataLakeServiceClient.GetFileSystemClient(blobContainer);
            }
        }

        /// <summary>
        /// Gets a default DataLakeFileSystemClient.
        /// </summary>
        /// <returns>A DataLakeFileSystemClient with default settings.</returns>
        public static DataLakeFileSystemClient GetDefaultDataLakeFileSystemClient()
        {
            var serviceClient = new DataLakeServiceClient("DefaultConnectionString");
            return serviceClient.GetFileSystemClient("DefaultFileSystemName");
        }

    }
}
