using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosListProviderContractTests : ListProviderContractTests
    {
        public CosmosListProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task CreateReadUpdateDelete() => CreateReadUpdateDeleteContractAsync();

        [CosmosFact]
        public Task AuthenticatedAccessAndDailyRenewal() =>
            AuthenticatedAccessAndDailyRenewalContractAsync();

        [CosmosFact]
        public Task ChannelMembership() => ChannelMembershipContractAsync();

        [CosmosFact]
        public Task ChannelAndVideoReadModels() => ChannelAndVideoReadModelsContractAsync();
    }
}
