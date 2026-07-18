using System.Linq;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializerTests
    {
        [Fact]
        public void ListsPolicyIncludesMandatoryRootPath()
        {
            var lists = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.Lists);

            Assert.Contains(
                lists.IndexingPolicy.IncludedPaths,
                path => path.Path == "/*");
            Assert.Contains(
                lists.IndexingPolicy.ExcludedPaths,
                path => path.Path == "/channels/*");
        }

        [Fact]
        public void SystemPolicyReliesOnBuiltInIdIndexAndExcludesUserPaths()
        {
            var system = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.System);

            Assert.Empty(system.IndexingPolicy.IncludedPaths);
            Assert.Equal(
                new[] { "/*" },
                system.IndexingPolicy.ExcludedPaths.Select(path => path.Path));
        }

        [Fact]
        public void ChannelsPolicySupportsDeterministicStaleOrdering()
        {
            var channels = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.Channels);

            var composite = Assert.Single(channels.IndexingPolicy.CompositeIndexes);
            Assert.Equal(new[] { "/staleAfter", "/id" }, composite.Select(path => path.Path));
            Assert.All(composite, path => Assert.Equal(
                Microsoft.Azure.Cosmos.CompositePathSortOrder.Ascending,
                path.Order));
        }
    }
}
