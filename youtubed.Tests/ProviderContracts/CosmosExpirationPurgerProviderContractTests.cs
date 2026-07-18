using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosExpirationPurgerProviderContractTests : ExpirationPurgerProviderContractTests
    {
        public CosmosExpirationPurgerProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosListProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task ExpiredListCleanup() => ExpiredListCleanupContractAsync();

        [CosmosFact]
        public Task ExpiredShareLinkCleanup() => ExpiredShareLinkCleanupContractAsync();

        [CosmosFact]
        public Task ExpiredChannelCleanup() => ExpiredChannelCleanupContractAsync();
    }
}
