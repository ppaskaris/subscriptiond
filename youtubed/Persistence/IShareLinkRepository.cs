using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Persistence
{
    public interface IShareLinkRepository
    {
        Task<bool> TryCreateAsync(ShareLinkModel shareLink);
        Task<IReadOnlyList<ShareLinkModel>> GetByListAsync(Guid listId);
        Task DeleteAsync(string password);
        Task DeleteByListAsync(Guid listId);
        Task<ConsumedShareLinkModel> ConsumeAsync(string password, DateTimeOffset now);
        Task<int> RemoveExpiredAsync(DateTimeOffset deleteBefore);
    }
}
