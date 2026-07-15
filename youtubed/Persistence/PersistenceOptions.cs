namespace youtubed.Persistence
{
    public sealed class PersistenceOptions
    {
        public const string SectionName = "Persistence";

        public PersistenceProvider Provider { get; set; } = PersistenceProvider.SqlServer;
    }
}
