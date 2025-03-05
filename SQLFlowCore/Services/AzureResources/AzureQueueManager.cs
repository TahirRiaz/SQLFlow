using Azure;
using Azure.Storage;
using Azure.Storage.Queues;
using System;
using System.Collections.Generic;

namespace SQLFlowCore.Services.AzureResources
{
    /// <summary>
    /// Manages Azure Queues.
    /// </summary>
    public class AzureQueueManager
    {
        private readonly string _accountName;
        private readonly StorageSharedKeyCredential _credential;
        /// <summary>
        /// Initializes a new instance of the <see cref="AzureQueueManager"/> class.
        /// </summary>
        /// <param name="accountName">The name of the Azure Storage account.</param>
        /// <param name="accountKey">The key for the Azure Storage account.</param>
        public AzureQueueManager(string accountName, string accountKey)
        {
            _accountName = accountName;
            _credential = new StorageSharedKeyCredential(accountName, accountKey);
        }
        /// <summary>
        /// Creates a new queue or returns an existing one.
        /// </summary>
        /// <param name="queueName">The name of the queue.</param>
        /// <returns>The <see cref="QueueClient"/> for the queue.</returns>
        public QueueClient CreateQueue(string queueName)
        {
            var queueUri = new Uri($"https://{_accountName}.queue.core.windows.net/{queueName}");

            var options = new QueueClientOptions
            {
                Diagnostics = { IsLoggingEnabled = true }
            };

            var queueClient = new QueueClient(queueUri, _credential, options);
            Response response = queueClient.CreateIfNotExists();



            if (response != null)
            {
                // Handle response here if needed.
                // For example, check if the queue was newly created or already existed.
            }

            return queueClient;
        }

        /// <summary>
        /// Checks if a queue exists.
        /// </summary>
        /// <param name="queueName">The name of the queue.</param>
        /// <returns>True if the queue exists, false otherwise.</returns>
        public bool QueueExists(string queueName)
        {
            var queueUri = new Uri($"https://{_accountName}.queue.core.windows.net/{queueName}");

            var queueClient = new QueueClient(queueUri, _credential);
            try
            {
                queueClient.GetProperties();
                return true;
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "QueueNotFound" || ex.ErrorCode == "InvalidResourceName")
            {
                return false;
            }
        }

        /// <summary>
        /// Gets all queues in the Azure Storage account.
        /// </summary>
        /// <returns>An <see cref="IEnumerable{T}"/> of queue names.</returns>
        public IEnumerable<string> GetAllQueues()
        {
            var serviceUri = new Uri($"https://{_accountName}.queue.core.windows.net");
            var serviceClient = new QueueServiceClient(serviceUri, _credential);

            var queues = new List<string>();
            foreach (var queueItem in serviceClient.GetQueues())
            {
                queues.Add(queueItem.Name);
            }

            return queues;
        }
    }



}