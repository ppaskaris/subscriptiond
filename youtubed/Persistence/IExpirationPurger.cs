using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence
{
    public interface IExpirationPurger
    {
        Task<int> PurgeExpiredListsAsync(CancellationToken cancellationToken);
        Task<int> PurgeExpiredShareLinksAsync(CancellationToken cancellationToken);
        Task<int> PurgeExpiredChannelsAsync(CancellationToken cancellationToken);
    }
}
