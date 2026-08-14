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
            : base(new CosmosProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task CreateAndList() => CreateAndListContractAsync();

        [CosmosFact]
        public Task Consume() => ConsumeContractAsync();

        [CosmosFact]
        public Task Delete() => DeleteContractAsync();
    }
}
