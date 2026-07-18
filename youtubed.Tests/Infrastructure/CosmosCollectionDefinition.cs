using Xunit;

namespace youtubed.Tests.Infrastructure
{
    [CollectionDefinition(CosmosTestFixture.CollectionName, DisableParallelization = true)]
    public sealed class CosmosCollectionDefinition : ICollectionFixture<CosmosTestFixture>
    {
    }
}
