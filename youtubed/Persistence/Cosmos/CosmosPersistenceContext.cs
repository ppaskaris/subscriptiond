using System;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosPersistenceContext
    {
        public CosmosPersistenceContext(CosmosClient client, IOptions<CosmosOptions> options)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);
            var value = options.Value;

            Database = client.GetDatabase(value.DatabaseName);
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
