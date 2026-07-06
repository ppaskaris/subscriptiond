using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IChannelRepository
    {
        Task<ChannelModel> GetByIdAsync(string id);
        Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter);
        Task UpdateMetadataAsync(string id, string url, string title, string thumbnail, string playlistId);
        Task MarkUnavailableAsync(string id, ChannelStatusReason reason, DateTimeOffset statusUpdatedAt, DateTimeOffset staleAfter);
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
