using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosContainerSetupTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosContainerSetupTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task CreatesTtlEnabledContainersWithNarrowedIndexes()
        {
            var lists = (await _fixture.GetContainer(CosmosTestFixture.ListsContainerName).ReadContainerAsync()).Resource;
            var channels = (await _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName).ReadContainerAsync()).Resource;
            var shareLinks = (await _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName).ReadContainerAsync()).Resource;
            var system = (await _fixture.GetContainer(CosmosTestFixture.SystemContainerName).ReadContainerAsync()).Resource;

            Assert.Equal(-1, lists.DefaultTimeToLive);
            Assert.Contains(lists.IndexingPolicy.ExcludedPaths, path => path.Path == "/channels/*");
            Assert.Equal(-1, channels.DefaultTimeToLive);
            Assert.Contains(channels.IndexingPolicy.IncludedPaths, path => path.Path == "/staleAfter/?");
            Assert.Contains(channels.IndexingPolicy.ExcludedPaths, path => path.Path == "/videos/*");
            Assert.Contains(
                channels.IndexingPolicy.CompositeIndexes,
                index => index.Count == 2
                    && index[0].Path == "/staleAfter"
                    && index[1].Path == "/id");
            Assert.Equal(-1, shareLinks.DefaultTimeToLive);
            Assert.Contains(shareLinks.IndexingPolicy.IncludedPaths, path => path.Path == "/listId/?");
            Assert.Null(system.DefaultTimeToLive);
            Assert.Empty(system.IndexingPolicy.IncludedPaths);
            Assert.Contains(system.IndexingPolicy.ExcludedPaths, path => path.Path == "/*");
        }
    }
}
