using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosProjectionProviderContractTests : ChannelAndProjectionProviderContractTests
    {
        public CosmosProjectionProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosListProviderContractTestFixture(
                fixture,
                projectRefreshResults: false))
        {
        }

        [CosmosFact]
        public Task ProjectionUpdate() => ProjectionUpdateContractAsync();
    }
}
