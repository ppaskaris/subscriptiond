using System.Threading;
using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Persistence
{
    public sealed class SqlExpirationPurger : IExpirationPurger
    {
        private readonly IListRepository _listRepository;
        private readonly IShareLinkRepository _shareLinkRepository;
        private readonly IChannelRepository _channelRepository;
        private readonly IAppClock _clock;

        public SqlExpirationPurger(
            IListRepository listRepository,
            IShareLinkRepository shareLinkRepository,
            IChannelRepository channelRepository,
            IAppClock clock)
        {
            _listRepository = listRepository;
            _shareLinkRepository = shareLinkRepository;
            _channelRepository = channelRepository;
            _clock = clock;
        }

        public Task<int> PurgeExpiredListsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _listRepository.RemoveExpiredAsync(_clock.UtcNow);
        }

        public Task<int> PurgeExpiredShareLinksAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _shareLinkRepository.RemoveExpiredAsync(
                _clock.UtcNow.Subtract(Constants.ShareLinkRetentionAfterExpiration));
        }

        public Task<int> PurgeExpiredChannelsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _channelRepository.RemoveOrphanChannelsAsync(_clock.UtcNow);
        }
    }
}
