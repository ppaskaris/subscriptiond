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
        Task<ListModel> GetAuthenticatedListAsync(Guid id, string token);
        Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token);
        Task<ListViewModel> GetListViewAsync(Guid id);
        Task<ListViewModel> GetListViewAsync(ListModel list);
        Task<ListViewModel> GetListChannelViewAsync(Guid id);
        Task<ListViewModel> GetListChannelViewAsync(ListModel list);

        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);

        Task UpdateListAsync(Guid id, string title, decimal playbackRate);
        Task DeleteListAsync(Guid id);
    }
}
