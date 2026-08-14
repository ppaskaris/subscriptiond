using System;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosPersistenceContext
    {
        public CosmosPersistenceContext(CosmosClient client, CosmosOptions options)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);

            Database = client.GetDatabase(options.DatabaseName);
            Lists = Database.GetContainer(CosmosContainerNames.Lists);
            Channels = Database.GetContainer(CosmosContainerNames.Channels);
            ShareLinks = Database.GetContainer(CosmosContainerNames.ShareLinks);
        }

        public Database Database { get; }
        public Container Lists { get; }
        public Container Channels { get; }
        public Container ShareLinks { get; }
    }
}
