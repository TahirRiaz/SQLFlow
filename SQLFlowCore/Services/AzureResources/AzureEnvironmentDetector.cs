using System;
using System.Collections.Generic;
using Azure.Identity;
using System.Linq;

namespace SQLFlowCore.Services.CloudResources
{
    public enum CloudProvider
    {
        Unknown,
        Azure,
        AWS
    }

    public class CloudEnvironmentDetector
    {
        // Common environment variables for cloud environments
        private static readonly Dictionary<CloudProvider, string[]> CloudEnvironmentVariables = new Dictionary<CloudProvider, string[]>
        {
            {
                CloudProvider.Azure, new[]
                {
                    "WEBSITE_INSTANCE_ID",              // Azure App Service
                    "FUNCTIONS_WORKER_RUNTIME",         // Azure Functions
                    "AZURE_CONTAINER_INSTANCE_ROOT_PATH", // Azure Container Instance
                    "KUBERNETES_SERVICE_HOST",          // AKS or Kubernetes (can also be AWS EKS)
                    "AZURE_VM_RESOURCE_GROUP",          // Azure VM
                    "MSI_ENDPOINT",                     // Managed Service Identity (MSI)
                    "MSI_SECRET",                       // Managed Service Identity (MSI)
                    "IDENTITY_HEADER",                  // Managed Service Identity (MSI)
                    "APPSETTING_WEBSITE_SITE_NAME"      // Azure App Service
                }
            },
            {
                CloudProvider.AWS, new[]
                {
                    "AWS_REGION",                       // AWS Region
                    "AWS_EXECUTION_ENV",                // AWS Lambda
                    "AWS_LAMBDA_FUNCTION_NAME",         // AWS Lambda
                    "ECS_CONTAINER_METADATA_URI",       // AWS ECS
                    "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", // AWS ECS/EKS
                    "EC2_INSTANCE_ID",                  // AWS EC2 
                    "AWS_DEFAULT_REGION",               // AWS Default Region
                    "AWS_ACCESS_KEY_ID",                // AWS Credentials (not recommended for detection, but included)
                    "EC2_METADATA_SERVICE_ENDPOINT"     // EC2 Metadata service
                }
            }
        };

        // Custom environment variables that can be set manually
        private static readonly Dictionary<CloudProvider, string> CustomEnvironmentFlags = new Dictionary<CloudProvider, string>
        {
            { CloudProvider.Azure, "IS_RUNNING_IN_AZURE" },
            { CloudProvider.AWS, "IS_RUNNING_IN_AWS" }
        };

        /// <summary>
        /// Determines which cloud provider environment the application is running in
        /// </summary>
        /// <returns>The detected cloud provider or Unknown if not detected</returns>
        public static CloudProvider DetectCloudProvider()
        {
            foreach (var provider in CloudEnvironmentVariables.Keys)
            {
                if (IsRunningInCloudEnvironment(provider))
                {
                    Console.WriteLine($"Detected {provider} environment.");
                    return provider;
                }
            }

            Console.WriteLine("No cloud environment detected. Assuming local or non-cloud environment.");
            return CloudProvider.Unknown;
        }

        /// <summary>
        /// Checks if running in a specific cloud provider environment
        /// </summary>
        /// <param name="provider">The cloud provider to check for</param>
        /// <returns>True if running in the specified environment, false otherwise</returns>
        public static bool IsRunningInCloudEnvironment(CloudProvider provider)
        {
            // Check standard environment variables
            if (CloudEnvironmentVariables.TryGetValue(provider, out string[] envVars))
            {
                foreach (var envVar in envVars)
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
                    {
                        Console.WriteLine($"Detected {provider} environment via {envVar}.");
                        return true;
                    }
                }
            }

            // Check for a custom environment variable
            if (CustomEnvironmentFlags.TryGetValue(provider, out string customEnvVar))
            {
                string customFlag = Environment.GetEnvironmentVariable(customEnvVar);
                if (!string.IsNullOrEmpty(customFlag))
                {
                    Console.WriteLine($"Detected custom {provider} environment flag via {customEnvVar}.");
                    return customFlag.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the appropriate Azure credential options based on the environment
        /// </summary>
        public static DefaultAzureCredentialOptions GetDefaultAzureCredentialOptions()
        {
            bool isRunningInAzure = IsRunningInCloudEnvironment(CloudProvider.Azure);

            // Configure DefaultAzureCredential options
            var credentialOptions = new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = false,
                ExcludeManagedIdentityCredential = !isRunningInAzure,
                ExcludeSharedTokenCacheCredential = false,
                ExcludeAzureCliCredential = false,
                ExcludeVisualStudioCredential = false,
                ExcludeVisualStudioCodeCredential = false,
                ExcludeInteractiveBrowserCredential = false
            };

            return credentialOptions;
        }

        /// <summary>
        /// Gets a dictionary of environment variables for the detected cloud provider
        /// </summary>
        /// <returns>Dictionary of environment variable names and values</returns>
        public static Dictionary<string, string> GetCloudEnvironmentVariables()
        {
            var provider = DetectCloudProvider();
            var result = new Dictionary<string, string>();

            if (provider != CloudProvider.Unknown && CloudEnvironmentVariables.TryGetValue(provider, out string[] envVars))
            {
                foreach (var envVar in envVars)
                {
                    var value = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(envVar, value);
                    }
                }
            }

            return result;
        }
    }
}