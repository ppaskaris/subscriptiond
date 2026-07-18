namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosOptions
    {
        public const string SectionName = "Cosmos";

        public string Endpoint { get; set; }

        public string Key { get; set; }

        public string DatabaseName { get; set; } = "youtubed";

        public string ListsContainer { get; set; } = CosmosContainerNames.Lists;

        public string ChannelsContainer { get; set; } = CosmosContainerNames.Channels;

        public string ShareLinksContainer { get; set; } = CosmosContainerNames.ShareLinks;

        public string SystemContainer { get; set; } = CosmosContainerNames.System;
    }
}
