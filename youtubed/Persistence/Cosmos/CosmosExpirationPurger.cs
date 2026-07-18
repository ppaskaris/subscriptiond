using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosExpirationPurger : IExpirationPurger
    {
        public Task<int> PurgeExpiredListsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<int> PurgeExpiredShareLinksAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<int> PurgeExpiredChannelsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }
}
