using System.Threading.Tasks;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class SqlProviderContractTestFixture : IProviderContractTestFixture
    {
        private readonly LocalDbTestFixture _fixture;

        public SqlProviderContractTestFixture(LocalDbTestFixture fixture)
        {
            _fixture = fixture;
        }

        public string ProviderName => "SqlServer";

        public Task ResetAsync()
        {
            return _fixture.ResetAsync();
        }

        public ProviderContractTestContext CreateContext(IAppClock clock)
        {
            var lists = new ListRepository(_fixture.ConnectionFactory);
            var channels = new ChannelRepository(_fixture.ConnectionFactory);
            var shareLinks = new ShareLinkRepository(_fixture.ConnectionFactory);

            return new ProviderContractTestContext(
                lists,
                channels,
                shareLinks,
                new SqlListProjectionRepository(),
                new WorkerStateRepository(_fixture.ConnectionFactory, clock),
                new SqlExpirationPurger(lists, shareLinks, channels, clock));
        }
    }
}
