using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosShareLinkRepository : IShareLinkRepository
    {
        private const int MaxWriteAttempts = 2;

        private readonly Container _shareLinks;
        private readonly Container _lists;
        private readonly IAppClock _clock;

        public CosmosShareLinkRepository(
            Container shareLinks,
            Container lists,
            IAppClock clock)
        {
            _shareLinks = shareLinks ?? throw new ArgumentNullException(nameof(shareLinks));
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<bool> TryCreateAsync(ShareLink shareLink)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ShareCreate);
            var document = CosmosDocumentMapper.ToDocument(shareLink, _clock.UtcNow);

            try
            {
                await _shareLinks.CreateItemAsync(document, new PartitionKey(document.Id));
                return true;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ShareList);
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.listId = @listId")
                .WithParameter("@listId", listId.ToString("D"));
            var links = new List<ShareLink>();
            using var iterator = _shareLinks.GetItemQueryIterator<CosmosShareLinkDocument>(query);

            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync())
                {
                    links.Add(CosmosDocumentMapper.ToShareLink(document));
                }
            }

            return links
                .OrderByDescending(link => link.CreatedAt)
                .ThenBy(link => link.Password, StringComparer.Ordinal)
                .ToArray();
        }

        public async Task DeleteAsync(Guid listId, string password)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ShareDelete);
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadShareLinkAsync(password);
                if (document == null || !ListIdsEqual(document.ListId, listId))
                {
                    return;
                }

                try
                {
                    await _shareLinks.DeleteItemAsync<CosmosShareLinkDocument>(
                        password,
                        new PartitionKey(password),
                        new ItemRequestOptions { IfMatchEtag = document.ETag });
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }
        }

        public async Task DeleteByListAsync(Guid listId)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ShareDelete);
            var links = await GetByListAsync(listId);
            foreach (var link in links)
            {
                await DeleteAsync(listId, link.Password);
            }
        }

        public async Task<ConsumedShareLink> ConsumeAsync(
            string password,
            DateTimeOffset now)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ShareConsume);
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var shareLink = await ReadShareLinkAsync(password);
                if (shareLink == null
                    || shareLink.UsedAt.HasValue
                    || shareLink.ExpiresAfter <= now
                    || !Guid.TryParse(shareLink.ListId, out var listId))
                {
                    return null;
                }

                var list = await ReadListAsync(shareLink.ListId);
                if (list == null)
                {
                    return null;
                }

                shareLink.UsedAt = now;
                shareLink.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    shareLink.ExpiresAfter + Constants.ShareLinkRetentionAfterExpiration,
                    _clock.UtcNow);

                try
                {
                    await _shareLinks.ReplaceItemAsync(
                        shareLink,
                        shareLink.Id,
                        new PartitionKey(shareLink.Id),
                        new ItemRequestOptions { IfMatchEtag = shareLink.ETag });
                    return new ConsumedShareLink
                    {
                        ListId = listId,
                        Token = list.Token
                    };
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }

            return null;
        }

        public Task<int> RemoveExpiredAsync(DateTimeOffset deleteBefore)
        {
            return Task.FromResult(0);
        }

        private async Task<CosmosShareLinkDocument> ReadShareLinkAsync(string password)
        {
            try
            {
                var response = await _shareLinks.ReadItemAsync<CosmosShareLinkDocument>(
                    password,
                    new PartitionKey(password));
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task<CosmosListDocument> ReadListAsync(string listId)
        {
            try
            {
                var response = await _lists.ReadItemAsync<CosmosListDocument>(
                    listId,
                    new PartitionKey(listId));
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private static bool ListIdsEqual(string documentListId, Guid listId)
        {
            return Guid.TryParse(documentListId, out var parsedListId)
                && parsedListId == listId;
        }
    }
}
