using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IChannelRepository
    {
        Task<Channel> GetByIdAsync(string id);
        Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter);
        Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
            DateTimeOffset now,
            int take,
            CancellationToken cancellationToken);
        Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
            CancellationToken cancellationToken);
        Task<IReadOnlyList<Channel>> GetBatchAsync(
            IReadOnlyCollection<string> channelIds,
            CancellationToken cancellationToken);
        Task SaveRefreshResultsAsync(
            IReadOnlyCollection<ChannelRefreshResult> results,
            CancellationToken cancellationToken);
        Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now);
    }
}
