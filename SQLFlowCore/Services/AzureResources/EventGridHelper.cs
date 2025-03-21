using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.EventGrid;
using Azure.ResourceManager.EventGrid.Models;
using Azure.ResourceManager.Resources;

namespace SQLFlowCore.Services.AzureResources
{
    internal class EventGridManager
    {
        private readonly ArmClient _armClient;
        private readonly string _subscriptionId;

        // Initialize with Azure credentials (DefaultAzureCredential or custom TokenCredential) and target subscription
        public EventGridManager(string subscriptionId, TokenCredential credential = null)
        {
            _subscriptionId = subscriptionId ?? throw new ArgumentNullException(nameof(subscriptionId));
            credential ??= new DefaultAzureCredential();
            _armClient = new ArmClient(credential);
        }

        /// <summary>
        /// List all Event Grid Topics in the specified resource group.
        /// </summary>
        public async Task<IReadOnlyList<EventGridTopicResource>> ListTopicsAsync(string resourceGroupName)
        {
            if (string.IsNullOrEmpty(resourceGroupName))
                throw new ArgumentNullException(nameof(resourceGroupName));

            try
            {
                // Get the resource group resource
                ResourceIdentifier rgId = ResourceGroupResource.CreateResourceIdentifier(_subscriptionId, resourceGroupName);
                ResourceGroupResource resourceGroup = _armClient.GetResourceGroupResource(rgId);

                // Get the collection of Event Grid topics in this resource group
                EventGridTopicCollection topicCollection = resourceGroup.GetEventGridTopics();

                // Retrieve all topics (as a list)
                List<EventGridTopicResource> topics = new List<EventGridTopicResource>();
                await foreach (EventGridTopicResource topic in topicCollection.GetAllAsync())
                {
                    topics.Add(topic);
                }
                return topics;
            }
            catch (RequestFailedException ex)
            {
                // Handle Azure request failures (e.g., resource group not found, permission issues)
                throw new Exception($"Failed to list Event Grid topics in resource group '{resourceGroupName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a new Event Grid Topic in the specified resource group.
        /// </summary>
        public async Task<EventGridTopicResource> CreateTopicAsync(string resourceGroupName, string topicName, string location)
        {
            if (string.IsNullOrEmpty(resourceGroupName))
                throw new ArgumentNullException(nameof(resourceGroupName));
            if (string.IsNullOrEmpty(topicName))
                throw new ArgumentNullException(nameof(topicName));
            if (string.IsNullOrEmpty(location))
                throw new ArgumentNullException(nameof(location));

            try
            {
                // Get the resource group resource
                ResourceIdentifier rgId = ResourceGroupResource.CreateResourceIdentifier(_subscriptionId, resourceGroupName);
                ResourceGroupResource resourceGroup = _armClient.GetResourceGroupResource(rgId);

                // Prepare the topic data (set location and any optional settings like tags or network access)
                EventGridTopicData topicData = new EventGridTopicData(new AzureLocation(location));
                // Example: topicData.PublicNetworkAccess = EventGridPublicNetworkAccess.Enabled;
                // Add any other properties as needed (e.g., inbound IP rules, tags).

                // Create or update the Event Grid topic and wait for completion
                ArmOperation<EventGridTopicResource> operation = await resourceGroup.GetEventGridTopics()
                    .CreateOrUpdateAsync(WaitUntil.Completed, topicName, topicData);
                EventGridTopicResource topicResource = operation.Value;
                return topicResource;
            }
            catch (RequestFailedException ex)
            {
                throw new Exception($"Failed to create Event Grid topic '{topicName}' in resource group '{resourceGroupName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if an Event Grid Topic exists in the specified resource group.
        /// </summary>
        public async Task<bool> TopicExistsAsync(string resourceGroupName, string topicName)
        {
            if (string.IsNullOrEmpty(resourceGroupName))
                throw new ArgumentNullException(nameof(resourceGroupName));
            if (string.IsNullOrEmpty(topicName))
                throw new ArgumentNullException(nameof(topicName));

            try
            {
                // Get the resource group and topic collection
                ResourceIdentifier rgId = ResourceGroupResource.CreateResourceIdentifier(_subscriptionId, resourceGroupName);
                ResourceGroupResource resourceGroup = _armClient.GetResourceGroupResource(rgId);
                EventGridTopicCollection topicCollection = resourceGroup.GetEventGridTopics();

                // Check existence of the topic by name
                bool exists = await topicCollection.ExistsAsync(topicName);
                return exists;
            }
            catch (RequestFailedException ex)
            {
                throw new Exception($"Failed to check existence of Event Grid topic '{topicName}' in resource group '{resourceGroupName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a new Event Subscription on a given Event Grid Topic, with a Storage Queue as the destination.
        /// Supports adding advanced filters to the subscription.
        /// </summary>
        /// <param name="resourceGroupName">The resource group of the Event Grid topic.</param>
        /// <param name="topicName">The name of the Event Grid topic to subscribe to.</param>
        /// <param name="eventSubscriptionName">The name of the new event subscription.</param>
        /// <param name="storageAccountId">
        /// The Azure Resource ID of the Storage Account that contains the destination Queue (see BuildStorageQueueResourceId helper).
        /// </param>
        /// <param name="queueName">The name of the Storage Queue to which events will be sent.</param>
        /// <param name="advancedFilters">Optional list of advanced filters to apply (e.g., StringContainsAdvancedFilter, NumberInAdvancedFilter, etc.).</param>
        public async Task<TopicEventSubscriptionResource> CreateEventSubscriptionAsync(
            string resourceGroupName,
            string topicName,
            string eventSubscriptionName,
            string storageAccountId,
            string queueName,
            IList<AdvancedFilter> advancedFilters = null)
        {
            if (string.IsNullOrEmpty(resourceGroupName))
                throw new ArgumentNullException(nameof(resourceGroupName));
            if (string.IsNullOrEmpty(topicName))
                throw new ArgumentNullException(nameof(topicName));
            if (string.IsNullOrEmpty(eventSubscriptionName))
                throw new ArgumentNullException(nameof(eventSubscriptionName));
            if (string.IsNullOrEmpty(storageAccountId))
                throw new ArgumentNullException(nameof(storageAccountId));
            if (string.IsNullOrEmpty(queueName))
                throw new ArgumentNullException(nameof(queueName));

            try
            {
                // Get the Event Grid topic resource
                ResourceIdentifier topicId = EventGridTopicResource.CreateResourceIdentifier(_subscriptionId, resourceGroupName, topicName);
                EventGridTopicResource topicResource = _armClient.GetEventGridTopicResource(topicId);

                // Define the Storage Queue destination for the event subscription
                var queueDestination = new StorageQueueEventSubscriptionDestination
                {
                    ResourceId = new ResourceIdentifier(storageAccountId),  // Convert string to ResourceIdentifier
                    QueueName = queueName
                };

                // Define event subscription filter (with advanced filters if provided)
                var eventFilter = new EventSubscriptionFilter();
                if (advancedFilters != null && advancedFilters.Count > 0)
                {
                    foreach (AdvancedFilter af in advancedFilters)
                    {
                        eventFilter.AdvancedFilters.Add(af);
                    }
                }
                // (Optional) Set additional filter properties, e.g.:
                // eventFilter.SubjectBeginsWith = "SomePrefix";
                // eventFilter.SubjectEndsWith = ".jpg";
                // eventFilter.IsSubjectCaseSensitive = false;

                // Create the event subscription data model
                var eventSubscriptionData = new EventGridSubscriptionData
                {
                    Destination = queueDestination,
                    Filter = eventFilter
                    // You can also set labels, dead-letter endpoint, etc., if needed
                };

                // Create or update the event subscription on the topic
                TopicEventSubscriptionCollection subscriptions = topicResource.GetTopicEventSubscriptions();
                ArmOperation<TopicEventSubscriptionResource> operation = await subscriptions
                    .CreateOrUpdateAsync(WaitUntil.Completed, eventSubscriptionName, eventSubscriptionData);
                TopicEventSubscriptionResource result = operation.Value;
                return result;
            }
            catch (RequestFailedException ex)
            {
                throw new Exception($"Failed to create event subscription '{eventSubscriptionName}' on topic '{topicName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Helper to build the resource ID for a Storage Account (used for queue destination).
        /// The event subscription destination requires the storage account's Azure resource ID.
        /// </summary>
        public static string BuildStorageQueueResourceId(string subscriptionId, string storageAccountResourceGroup, string storageAccountName)
        {
            if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(storageAccountResourceGroup) || string.IsNullOrEmpty(storageAccountName))
            {
                throw new ArgumentException("SubscriptionId, resource group and account name must be provided.");
            }
            // Format: /subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{accountName}
            return $"/subscriptions/{subscriptionId}/resourceGroups/{storageAccountResourceGroup}/providers/Microsoft.Storage/storageAccounts/{storageAccountName}";
        }
    }
}

