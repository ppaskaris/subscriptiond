using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlShareLinkProviderContractTests : ShareLinkProviderContractTests
    {
        public SqlShareLinkProviderContractTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public Task CreateAndList() => CreateAndListContractAsync();

        [LocalDbFact]
        public Task Consume() => ConsumeContractAsync();

        [LocalDbFact]
        public Task Delete() => DeleteContractAsync();
    }
}
