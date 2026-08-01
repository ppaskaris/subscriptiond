using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IShareLinkRepository
    {
        Task<bool> TryCreateAsync(ShareLink shareLink);
        Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId);
        Task DeleteAsync(Guid listId, string password);
        Task DeleteByListAsync(Guid listId);
        Task<ConsumedShareLink> ConsumeAsync(string password, DateTimeOffset now);
        Task<int> RemoveExpiredAsync(DateTimeOffset deleteBefore);
    }
}
