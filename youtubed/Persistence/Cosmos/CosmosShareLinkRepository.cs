using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ILogger<CosmosShareLinkRepository> _logger;

        public CosmosShareLinkRepository(
            CosmosPersistenceContext context,
            IAppClock clock,
            ILogger<CosmosShareLinkRepository> logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> TryCreateAsync(ShareLink shareLink)
        {
            ArgumentNullException.ThrowIfNull(shareLink);
            var document = CosmosDocumentMapper.ToDocument(shareLink, _clock.UtcNow);
            try
            {
                await ExecuteAsync(
                    "create",
                    CosmosContainerNames.ShareLinks,
                    () => _context.ShareLinks.CreateItemAsync(
                        document,
                        new PartitionKey(document.Id)));
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
                var response = await ExecuteFeedAsync(
                    "query",
                    CosmosContainerNames.ShareLinks,
                    () => iterator.ReadNextAsync(CancellationToken.None));
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
            var item = await ReadShareAsync(password);
            return item == null
                ? null
                : CosmosDocumentMapper.ToShareLink(item.Resource);
        }

        public async Task DeleteAsync(Guid listId, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            for (var attempt = 0; attempt <= 1; attempt++)
            {
                var current = await ReadShareAsync(password);
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
                    await DeleteShareAsync(password, current.ETag);
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
            var share = await ReadShareAsync(password);
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
                await ExecuteAsync(
                    "replace",
                    CosmosContainerNames.ShareLinks,
                    () => _context.ShareLinks.ReplaceItemAsync(
                        share.Resource,
                        share.Resource.Id,
                        new PartitionKey(share.Resource.Id),
                        new ItemRequestOptions { IfMatchEtag = share.ETag }));
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

        private async Task<CosmosItem<CosmosShareLinkDocument>> ReadShareAsync(string password)
        {
            try
            {
                var response = await ExecuteAsync(
                    "pointRead",
                    CosmosContainerNames.ShareLinks,
                    () => _context.ShareLinks.ReadItemAsync<CosmosShareLinkDocument>(
                        password,
                        new PartitionKey(password)));
                return new CosmosItem<CosmosShareLinkDocument>(response.Resource, response.ETag);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task DeleteShareAsync(string password, string etag)
        {
            try
            {
                await ExecuteAsync(
                    "delete",
                    CosmosContainerNames.ShareLinks,
                    () => _context.ShareLinks.DeleteItemAsync<CosmosShareLinkDocument>(
                        password,
                        new PartitionKey(password),
                        new ItemRequestOptions { IfMatchEtag = etag }));
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Deletion is idempotent.
            }
        }

        private async Task<ItemResponse<T>> ExecuteAsync<T>(
            string operation,
            string container,
            Func<Task<ItemResponse<T>>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await action();
                LogRequest(operation, container, response.StatusCode, response.RequestCharge, stopwatch.Elapsed);
                return response;
            }
            catch (CosmosException exception)
            {
                LogRequest(operation, container, exception.StatusCode, exception.RequestCharge, stopwatch.Elapsed);
                throw;
            }
        }

        private async Task<FeedResponse<T>> ExecuteFeedAsync<T>(
            string operation,
            string container,
            Func<Task<FeedResponse<T>>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await action();
                LogRequest(operation, container, response.StatusCode, response.RequestCharge, stopwatch.Elapsed);
                return response;
            }
            catch (CosmosException exception)
            {
                LogRequest(operation, container, exception.StatusCode, exception.RequestCharge, stopwatch.Elapsed);
                throw;
            }
        }

        private void LogRequest(
            string operation,
            string container,
            HttpStatusCode status,
            double requestCharge,
            TimeSpan elapsed)
        {
            CosmosRepositoryClient.WriteTelemetry(
                _logger,
                operation,
                container,
                status,
                requestCharge,
                elapsed,
                retryCount: 0);
        }
    }
}
