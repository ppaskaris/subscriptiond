using System;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;

namespace youtubed.Services
{
    public interface IListService
    {
        Task<SubscriptionList> CreateListAsync(string title);
        Task<SubscriptionList> GetAuthenticatedListAsync(Guid id, string token);
        Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token);
        Task<ListViewModel> GetListChannelViewAsync(SubscriptionList list);
        Task ForceRefreshAsync(SubscriptionList list);

        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);

        Task UpdateListAsync(Guid id, string title, decimal playbackRate);
        Task DeleteListAsync(Guid id);
    }
}
