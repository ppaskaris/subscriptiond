using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Services
{
    public interface IShareLinkService
    {
        Task<ShareLinkModel> CreateShareLinkAsync(Guid listId);
        Task<IReadOnlyList<ShareLinkModel>> GetShareLinksAsync(Guid listId);
        Task DeleteShareLinkInListAsync(Guid listId, string password);
        Task DeleteShareLinksAsync(Guid listId);
        Task<ConsumedShareLinkModel> ConsumeShareLinkAsync(string password);
        Task<int> RemoveExpiredShareLinksAsync();
    }
}
