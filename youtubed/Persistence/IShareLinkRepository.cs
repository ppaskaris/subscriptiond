using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IShareLinkRepository
    {
        Task<bool> TryCreateAsync(ShareLink shareLink);
        Task<ShareLink> GetAsync(string password);
        Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId);
        Task DeleteAsync(Guid listId, string password);
        Task DeleteByListAsync(Guid listId);
        Task<bool> TryMarkUsedAsync(
            string password,
            Guid expectedListId,
            DateTimeOffset usedAt);
    }
}
