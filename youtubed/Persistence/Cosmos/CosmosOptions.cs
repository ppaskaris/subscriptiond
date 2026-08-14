namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosOptions
    {
        public const string SectionName = "Cosmos";

        public string ConnectionString { get; set; }

        public string DatabaseName { get; set; } = "youtubed";
    }
}
