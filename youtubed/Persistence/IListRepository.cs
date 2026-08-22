using System;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IListRepository
    {
        Task CreateAsync(SubscriptionList list);
        Task<SubscriptionList> GetAsync(Guid id);
        Task<ListVideoProjection> GetAuthenticatedVideoProjectionAsync(
            Guid id,
            byte[] token,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn,
            int videoLimit);
        Task RenewExpirationAsync(Guid id, DateTimeOffset expiredAfter, DateOnly renewedOn);
        Task<ListVideoProjection> GetVideoProjectionAsync(SubscriptionList list, int videoLimit);
        Task<ListChannelProjection> GetChannelProjectionAsync(SubscriptionList list);
        Task AddChannelAsync(Guid listId, string channelId);
        Task RemoveChannelAsync(Guid listId, string channelId);
        Task UpdateAsync(Guid id, string title, decimal playbackRate);
        Task DeleteAsync(Guid id);
    }
}
