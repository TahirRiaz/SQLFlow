using Microsoft.Azure.Management.EventGrid;
using Microsoft.Azure.Management.EventGrid.Models;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.AzureResources
{
    internal enum AdvancedFilterOperatorType
    {
        StringEquals,
        StringIn,
        StringNotIn,
        StringBeginsWith,
        StringEndsWith,
        StringContains,
        NumberEquals,
        NumberGreaterThan,
        NumberGreaterThanOrEquals,
        NumberLessThan,
        NumberLessThanOrEquals,
        NumberIn,
        NumberNotIn,
        BoolEquals,
        IsNull,
        IsNotNull
    }

    public class EventGridHelper
    {
        private static ServiceClientCredentials _serviceClientCredentials;
        private static string _tenantId = "";
        private static string _clientId = "";
        private static string _clientSecret = "";

        public EventGridHelper(string tenantId, string clientId, string clientSecret)
        {
            _tenantId = tenantId;
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        internal async Task<ServiceClientCredentials> GetServiceClientCredentials()
        {
            // Authenticate and create a client
            var serviceClientCredentials = await ApplicationTokenProvider.LoginSilentAsync(_tenantId, _clientId, _clientSecret);
            _serviceClientCredentials = serviceClientCredentials;
            return serviceClientCredentials;
        }

        internal bool CheckIfTopicExists(string SubscriptionId, string ResourceGroupName, string topicName)
        {
            using var eventGridManagementClient = new EventGridManagementClient(_serviceClientCredentials)
            {
                SubscriptionId = SubscriptionId
            };

            // List all topics in the resource group
            var topics = eventGridManagementClient.Topics.ListByResourceGroup(ResourceGroupName);

            // CheckForError if the desired topic exists in the list
            return topics.Any(topic => topic.Name == topicName);
        }


        internal IEnumerable<Topic> ListEventGridTopics(string subscriptionId, string resourceGroupName)
        {
            var eventGridClient = new EventGridManagementClient(_serviceClientCredentials)
            {
                SubscriptionId = subscriptionId
            };

            try
            {
                return eventGridClient.Topics.ListByResourceGroup(resourceGroupName).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                return new List<Topic>();
            }
        }

        internal async Task CreateTopicAsync(string subscriptionId, string resourceGroupName, string topicName, string location)
        {
            var eventGridClient = new EventGridManagementClient(_serviceClientCredentials)
            {
                SubscriptionId = subscriptionId
            };

            Topic topic = new Topic
            {
                Location = location
            };

            await eventGridClient.Topics.CreateOrUpdateAsync(resourceGroupName, topicName, topic);
        }

        /*
        internal async Task<EventSubscription> CreateEventSubscriptionForStorageQueueAsync(string subscriptionId,string resourceGroupName,string topicName,
                            string eventSubscriptionName,string storageAccountId, // The ID of the storage account containing the queue
                            string queueName,
                            AdvancedFilterOperatorType? filterType = null,
                            string filterValue = null)
        {
            var eventGridClient = new EventGridManagementClient(_serviceClientCredentials)
            {
                SubscriptionId = subscriptionId
            };

            var advancedFilters = new List<AdvancedFilter>();
            if (filterType.HasValue && filterKey != null && filterValues != null)
            {
                switch (filterType.Value)
                {
                    case AdvancedFilterOperatorType.StringEquals:
                        advancedFilters.Add(new StringEqualsAdvancedFilter(filterKey, filterValues));
                        break;
                    case AdvancedFilterOperatorType.StringIn:
                        advancedFilters.Add(new StringInAdvancedFilter(filterKey, filterValues));
                        break;
                    //... add cases for other filter types accordingly
                    case AdvancedFilterOperatorType.NumberEquals:
                        advancedFilters.Add(new NumberEqualsAdvancedFilter(filterKey, filterValues.Select(double.Parse).ToList()));
                        break;
                    case AdvancedFilterOperatorType.BoolEquals:
                        advancedFilters.Add(new BoolEqualsAdvancedFilter(filterKey, filterValues.Select(bool.Parse).ToList()));
                        break;
                        // ... and so on for other filter types
                }
            }

            var eventSubscriptionInfo = new EventSubscription
            {
                Destination = new StorageQueueEventSubscriptionDestination
                {
                    ResourceId = storageAccountId,
                    QueueName = queueName
                },
                Filter = advancedFilters.Any() ? new EventSubscriptionFilter
                {
                    AdvancedFilters = advancedFilters
                } : null,
            };

            string topicScope = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.EventGrid/topics/{topicName}";
            
            return await eventGridClient.EventSubscriptions.CreateOrUpdateAsync(topicScope, eventSubscriptionName, eventSubscriptionInfo);
        }
        */

        internal string ConstructQueueResourceId(string subscriptionId, string resourceGroupName, string accountName, string queueName)
        {
            ///subscriptions/44027f1c-05ae-4ed8-ae2f-fb4fffb3212c/resourceGroups/datawarehouse-west-rg-dev-test/providers/Microsoft.Storage/storageAccounts/dvhkolumbus
            return $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Storage/storageAccounts/{accountName}/queueServices/default/queues/{queueName}";
        }
    }
}

