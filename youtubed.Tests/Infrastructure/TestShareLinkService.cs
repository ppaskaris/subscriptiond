using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    internal sealed class TestShareLinkService : IShareLinkService
    {
        public const string ExistingPassword = "amber-forest-river-sky";

        public Task<ShareLinkModel> CreateShareLinkAsync(Guid listId)
        {
            return Task.FromResult(new ShareLinkModel
            {
                Password = ExistingPassword,
                ListId = listId,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAfter = DateTimeOffset.UtcNow.AddHours(1)
            });
        }

        public Task<IReadOnlyList<ShareLinkModel>> GetShareLinksAsync(Guid listId)
        {
            if (listId != TestListService.ExistingListId)
            {
                return Task.FromResult<IReadOnlyList<ShareLinkModel>>(Array.Empty<ShareLinkModel>());
            }

            return Task.FromResult<IReadOnlyList<ShareLinkModel>>(new[]
            {
                new ShareLinkModel
                {
                    Password = ExistingPassword,
                    ListId = listId,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    ExpiresAfter = DateTimeOffset.UtcNow.AddMinutes(50)
                }
            });
        }

        public Task DeleteShareLinksAsync(Guid listId) => Task.CompletedTask;

        public Task DeleteShareLinkInListAsync(Guid listId, string password) => Task.CompletedTask;

        public Task<ConsumedShareLinkModel> ConsumeShareLinkAsync(string password)
        {
            if (!string.Equals(password, ExistingPassword, StringComparison.Ordinal))
            {
                return Task.FromResult<ConsumedShareLinkModel>(null);
            }

            return Task.FromResult(new ConsumedShareLinkModel
            {
                ListId = TestListService.ExistingListId,
                Token = TestListService.ExistingTokenBytes
            });
        }

        public Task<int> RemoveExpiredShareLinksAsync() => Task.FromResult(0);
    }
}
