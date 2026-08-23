using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosShareLinkRepository : IShareLinkRepository
    {
        private readonly CosmosPersistenceContext _context;
        private readonly IAppClock _clock;
        private readonly CosmosOperationExecutor _executor;

        public CosmosShareLinkRepository(
            CosmosPersistenceContext context,
            IAppClock clock,
            ILogger<CosmosShareLinkRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _executor = new CosmosOperationExecutor(logger);
        }

        public async Task<bool> TryCreateAsync(ShareLink shareLink)
        {
            ArgumentNullException.ThrowIfNull(shareLink);
            var document = CosmosDocumentMapper.ToDocument(shareLink, _clock.UtcNow);
            try
            {
                await _executor.ExecuteItemAsync(
                    "create",
                    CosmosContainerNames.ShareLinks,
                    retryCount: 0,
                    cancellationToken => _context.ShareLinks.CreateItemAsync(
                        document,
                        new PartitionKey(document.Id),
                        cancellationToken: cancellationToken),
                    CancellationToken.None);
                return true;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId)
        {
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.listId = @listId")
                .WithParameter("@listId", listId.ToString("D"));
            var documents = new List<CosmosShareLinkDocument>();
            using var iterator = _context.ShareLinks.GetItemQueryIterator<CosmosShareLinkDocument>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
            while (iterator.HasMoreResults)
            {
                var response = await _executor.ExecuteFeedPageAsync(
                    "query",
                    CosmosContainerNames.ShareLinks,
                    retryCount: 0,
                    iterator.ReadNextAsync,
                    CancellationToken.None);
                documents.AddRange(response);
            }

            return documents
                .Select(CosmosDocumentMapper.ToShareLink)
                .OrderByDescending(link => link.CreatedAt)
                .ThenBy(link => link.Password, StringComparer.Ordinal)
                .ToArray();
        }

        public async Task<ShareLink> GetAsync(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            var item = await ReadShareAsync(password, retryCount: 0);
            return item == null
                ? null
                : CosmosDocumentMapper.ToShareLink(item.Resource);
        }

        public async Task DeleteAsync(Guid listId, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            for (var attempt = 0; attempt <= 1; attempt++)
            {
                var current = await ReadShareAsync(password, retryCount: attempt);
                if (current == null
                    || !string.Equals(
                        current.Resource.ListId,
                        listId.ToString("D"),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                try
                {
                    await DeleteShareAsync(password, current.ETag, retryCount: attempt);
                    return;
                }
                catch (CosmosException exception) when (
                    attempt == 0
                    && exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Reread and reapply once after a concurrent consume.
                }
            }
        }

        public async Task DeleteByListAsync(Guid listId)
        {
            var links = await GetByListAsync(listId);
            foreach (var link in links)
            {
                await DeleteAsync(listId, link.Password);
            }
        }

        public async Task<bool> TryMarkUsedAsync(
            string password,
            Guid expectedListId,
            DateTimeOffset usedAt)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            var share = await ReadShareAsync(password, retryCount: 0);
            if (share == null
                || share.Resource.UsedAt.HasValue
                || share.Resource.ExpiresAfter <= usedAt
                || !string.Equals(
                    share.Resource.ListId,
                    expectedListId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            share.Resource.UsedAt = usedAt;
            share.Resource.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                share.Resource.ExpiresAfter + Constants.ShareLinkRetentionAfterExpiration,
                _clock.UtcNow);
            try
            {
                await _executor.ExecuteItemAsync(
                    "replace",
                    CosmosContainerNames.ShareLinks,
                    retryCount: 0,
                    cancellationToken => _context.ShareLinks.ReplaceItemAsync(
                        share.Resource,
                        share.Resource.Id,
                        new PartitionKey(share.Resource.Id),
                        new ItemRequestOptions { IfMatchEtag = share.ETag },
                        cancellationToken),
                    CancellationToken.None);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.Conflict
                || exception.StatusCode == HttpStatusCode.PreconditionFailed
                || exception.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            return true;
        }

        private async Task<CosmosItem<CosmosShareLinkDocument>> ReadShareAsync(
            string password,
            int retryCount)
        {
            var response = await _executor.ExecuteItemAsync(
                "pointRead",
                CosmosContainerNames.ShareLinks,
                retryCount,
                cancellationToken => _context.ShareLinks.ReadItemAsync<CosmosShareLinkDocument>(
                    password,
                    new PartitionKey(password),
                    cancellationToken: cancellationToken),
                CancellationToken.None,
                returnNullOnNotFound: true);
            return response == null
                ? null
                : new CosmosItem<CosmosShareLinkDocument>(response.Resource, response.ETag);
        }

        private async Task DeleteShareAsync(string password, string etag, int retryCount)
        {
            try
            {
                await _executor.ExecuteItemAsync(
                    "delete",
                    CosmosContainerNames.ShareLinks,
                    retryCount,
                    cancellationToken => _context.ShareLinks.DeleteItemAsync<CosmosShareLinkDocument>(
                        password,
                        new PartitionKey(password),
                        new ItemRequestOptions { IfMatchEtag = etag },
                        cancellationToken),
                    CancellationToken.None);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Deletion is idempotent.
            }
        }
    }
}
