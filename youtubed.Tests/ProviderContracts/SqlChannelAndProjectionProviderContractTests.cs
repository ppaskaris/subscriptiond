using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlChannelAndProjectionProviderContractTests : ChannelAndProjectionProviderContractTests
    {
        public SqlChannelAndProjectionProviderContractTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public Task CanonicalChannelCreateReadUpdate() => CanonicalChannelCreateReadUpdateContractAsync();

        [LocalDbFact]
        public Task ProjectionUpdate() => ProjectionUpdateContractAsync();
    }
}
