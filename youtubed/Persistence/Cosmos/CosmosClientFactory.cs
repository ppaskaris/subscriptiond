using System;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    public static class CosmosClientFactory
    {
        public static CosmosClient Create(CosmosOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Cosmos:ConnectionString must be configured before the Cosmos client is used.");
            }

            return new CosmosClient(options.ConnectionString, CreateClientOptions());
        }

        internal static CosmosClientOptions CreateClientOptions()
        {
            return new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                Serializer = CosmosSystemTextJsonSerializer.Instance
            };
        }
    }
}
