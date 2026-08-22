using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosChannelProviderContractTests : ChannelProviderContractTests
    {
        public CosmosChannelProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task CanonicalChannelCreateReadUpdate() =>
            CanonicalChannelCreateReadUpdateContractAsync();

        [CosmosFact]
        public Task BatchReadReflectsCanonicalUpdates() =>
            BatchReadReflectsCanonicalUpdatesContractAsync();
    }
}
