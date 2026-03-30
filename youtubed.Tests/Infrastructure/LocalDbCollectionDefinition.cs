using Xunit;

namespace youtubed.Tests.Infrastructure
{
    [CollectionDefinition(LocalDbTestFixture.CollectionName)]
    public sealed class LocalDbCollectionDefinition : ICollectionFixture<LocalDbTestFixture>
    {
    }
}
