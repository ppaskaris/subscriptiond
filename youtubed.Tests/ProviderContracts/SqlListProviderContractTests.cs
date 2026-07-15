using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlListProviderContractTests : ListProviderContractTests
    {
        public SqlListProviderContractTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public Task CreateReadUpdateDelete() => CreateReadUpdateDeleteContractAsync();

        [LocalDbFact]
        public Task AuthenticatedAccessAndDailyRenewal() => AuthenticatedAccessAndDailyRenewalContractAsync();

        [LocalDbFact]
        public Task ChannelMembership() => ChannelMembershipContractAsync();

        [LocalDbFact]
        public Task ChannelAndVideoReadModels() => ChannelAndVideoReadModelsContractAsync();
    }
}
