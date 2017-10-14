using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Services
{
    public interface IListService
    {
        Task<ListModel> CreateListAsync(string title);
        Task<ListModel> GetListAsync(Guid id);
        Task<ListViewModel> GetListViewAsync(Guid id);

        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);

        Task RenameListAsync(Guid id, string title);
        Task DeleteListAsync(Guid id);

        Task<int> RemoveExpiredListsAsync();
    }
}
