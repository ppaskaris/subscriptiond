using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlExpirationPurgerProviderContractTests : ExpirationPurgerProviderContractTests
    {
        public SqlExpirationPurgerProviderContractTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public Task ExpiredListCleanup() => ExpiredListCleanupContractAsync();

        [LocalDbFact]
        public Task ExpiredShareLinkCleanup() => ExpiredShareLinkCleanupContractAsync();

        [LocalDbFact]
        public Task ExpiredChannelCleanup() => ExpiredChannelCleanupContractAsync();
    }
}
