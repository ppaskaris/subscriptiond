using System;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IListRepository
    {
        Task CreateAsync(SubscriptionList list);
        Task<SubscriptionList> GetAsync(Guid id);
        Task<SubscriptionList> RenewExpirationAsync(
            SubscriptionList list,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn);
        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);
        Task UpdateAsync(Guid id, string title, decimal playbackRate);
        Task DeleteAsync(Guid id);
    }
}
