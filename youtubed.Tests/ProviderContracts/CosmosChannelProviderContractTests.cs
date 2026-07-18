using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosChannelProviderContractTests : ChannelAndProjectionProviderContractTests
    {
        public CosmosChannelProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosChannelProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task CanonicalChannelCreateReadUpdate() => CanonicalChannelCreateReadUpdateContractAsync();

        [CosmosFact]
        public Task StaleLookahead() => StaleLookaheadContractAsync();

        [CosmosFact]
        public Task UnavailableChannelsAreExcludedFromRefresh() =>
            UnavailableChannelsAreExcludedFromRefreshContractAsync();

        [CosmosFact]
        public Task SubscriptionReferencesAndCount() => SubscriptionReferencesAndCountContractAsync();
    }

    internal sealed class CosmosChannelProviderContractTestFixture : IProviderContractTestFixture
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosChannelProviderContractTestFixture(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        public string ProviderName => "Cosmos";

        public ExpirationPurgeBehavior PurgeBehavior => ExpirationPurgeBehavior.NoOp;

        public async Task ResetAsync()
        {
            await DeleteAllAsync(_fixture.GetContainer(CosmosTestFixture.ListsContainerName));
            await DeleteAllAsync(_fixture.GetContainer(CosmosTestFixture.ChannelsContainerName));
            await DeleteAllAsync(_fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName));
        }

        public ProviderContractTestContext CreateContext(IAppClock clock)
        {
            var lists = _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
            var channels = _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName);
            var shareLinks = _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName);
            return new ProviderContractTestContext(
                new CosmosListRepository(lists, channels, clock),
                new CosmosChannelRepository(channels, lists, clock),
                new CosmosShareLinkRepository(shareLinks, lists, clock),
                null,
                null,
                null);
        }

        private static async Task DeleteAllAsync(Container container)
        {
            using var iterator = container.GetItemQueryIterator<string>("SELECT VALUE c.id FROM c");
            while (iterator.HasMoreResults)
            {
                foreach (var id in await iterator.ReadNextAsync())
                {
                    await container.DeleteItemAsync<object>(id, new PartitionKey(id));
                }
            }
        }
    }
}
