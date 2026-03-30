using System;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IListRepository
    {
        Task CreateAsync(ListModel list);
        Task<ListModel> GetAsync(Guid id);
        Task<ListViewModel> GetViewAsync(Guid id, DateTimeOffset expiredAfter, DateTimeOffset now);
        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);
        Task RenameAsync(Guid id, string title);
        Task DeleteAsync(Guid id);
        Task<int> RemoveExpiredAsync(DateTimeOffset now);
    }
}
