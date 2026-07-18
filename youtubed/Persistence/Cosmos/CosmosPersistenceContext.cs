using Microsoft.Azure.Cosmos;
using System;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosPersistenceContext
    {
        public CosmosPersistenceContext(CosmosClient client, CosmosOptions options)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);

            Database = client.GetDatabase(options.DatabaseName);
            Lists = Database.GetContainer(options.ListsContainer);
            Channels = Database.GetContainer(options.ChannelsContainer);
            ShareLinks = Database.GetContainer(options.ShareLinksContainer);
            System = Database.GetContainer(options.SystemContainer);
        }

        public Database Database { get; }

        public Container Lists { get; }

        public Container Channels { get; }

        public Container ShareLinks { get; }

        public Container System { get; }
    }
}
