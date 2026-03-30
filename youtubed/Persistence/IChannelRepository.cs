using System;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IChannelRepository
    {
        Task<ChannelModel> GetByUrlAsync(string url);
        Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter);
        Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter);
        Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now);
    }
}
