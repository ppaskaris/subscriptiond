using System;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IChannelRepository
    {
        Task<ChannelModel> GetByUrlAsync(string url);
        Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter);
        Task UpdateMetadataAsync(string id, string url, string title, string thumbnail, string playlistId);
        Task MarkUnavailableAsync(string id, ChannelStatusReason reason, DateTimeOffset statusUpdatedAt, DateTimeOffset staleAfter);
        Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter);
        Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now);
    }
}
