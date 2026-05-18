using System;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IListRepository
    {
        Task CreateAsync(ListModel list);
        Task<ListModel> GetAsync(Guid id);
        Task<ListVideoProjection> GetVideoProjectionAsync(Guid id, DateTimeOffset expiredAfter, int videoLimit);
        Task<ListChannelProjection> GetChannelProjectionAsync(Guid id, DateTimeOffset expiredAfter);
        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);
        Task UpdateAsync(Guid id, string title, decimal playbackRate);
        Task DeleteAsync(Guid id);
        Task<int> RemoveExpiredAsync(DateTimeOffset now);
    }
}
