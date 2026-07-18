using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosShareLinkProviderContractTests : ShareLinkProviderContractTests
    {
        public CosmosShareLinkProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosListProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task CreateAndList() => CreateAndListContractAsync();

        [CosmosFact]
        public Task Consume() => ConsumeContractAsync();

        [CosmosFact]
        public Task Delete() => DeleteContractAsync();

        [CosmosFact]
        public async Task Consume_DoesNotMarkLinkUsedWhenTargetListIsMissing()
        {
            var missingListId = Guid.NewGuid();
            var link = await CreateShareLinkAsync(missingListId, "missing-list");

            var consumed = await Provider.ShareLinks.ConsumeAsync(link.Password, Clock.UtcNow);
            var stored = Assert.Single(await Provider.ShareLinks.GetByListAsync(missingListId));

            Assert.Null(consumed);
            Assert.Null(stored.UsedAt);
        }
    }
}
