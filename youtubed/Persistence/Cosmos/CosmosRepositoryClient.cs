using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace youtubed.Persistence.Cosmos
{
    internal sealed record CosmosItem<T>(T Resource, string ETag);

    internal interface ICosmosRepositoryClient
    {
        Task<CosmosItem<CosmosListDocument>> CreateListAsync(
            CosmosListDocument document,
            int retryCount,
            CancellationToken cancellationToken);
        Task<CosmosItem<CosmosListDocument>> ReadListAsync(
            string id,
            int retryCount,
            CancellationToken cancellationToken);
        Task<CosmosItem<CosmosListDocument>> ReplaceListAsync(
            CosmosListDocument document,
            string etag,
            int retryCount,
            CancellationToken cancellationToken);
        Task DeleteListAsync(string id, CancellationToken cancellationToken);
        Task<CosmosItem<CosmosChannelDocument>> CreateChannelAsync(
            CosmosChannelDocument document,
            int retryCount,
            CancellationToken cancellationToken);
        Task<CosmosItem<CosmosChannelDocument>> ReadChannelAsync(
            string id,
            int retryCount,
            CancellationToken cancellationToken);
        Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken);
        Task<CosmosItem<CosmosChannelDocument>> ReplaceChannelAsync(
            CosmosChannelDocument document,
            string etag,
            int retryCount,
            CancellationToken cancellationToken);
    }

    internal sealed class CosmosRepositoryClient : ICosmosRepositoryClient
    {
        private static readonly EventId RequestEvent = new(4100, "CosmosRequest");

        private readonly CosmosPersistenceContext _context;
        private readonly ILogger _logger;

        public CosmosRepositoryClient(
            CosmosPersistenceContext context,
            ILogger logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<CosmosItem<CosmosListDocument>> CreateListAsync(
            CosmosListDocument document,
            int retryCount,
            CancellationToken cancellationToken) => CreateAsync(
                _context.Lists,
                CosmosContainerNames.Lists,
                document,
                document.Id,
                retryCount,
                cancellationToken);

        public Task<CosmosItem<CosmosListDocument>> ReadListAsync(
            string id,
            int retryCount,
            CancellationToken cancellationToken) => ReadAsync<CosmosListDocument>(
                _context.Lists,
                CosmosContainerNames.Lists,
                id,
                retryCount,
                cancellationToken);

        public Task<CosmosItem<CosmosListDocument>> ReplaceListAsync(
            CosmosListDocument document,
            string etag,
            int retryCount,
            CancellationToken cancellationToken) => ReplaceAsync(
                _context.Lists,
                CosmosContainerNames.Lists,
                document,
                document.Id,
                etag,
                retryCount,
                cancellationToken);

        public async Task DeleteListAsync(string id, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await _context.Lists.DeleteItemAsync<CosmosListDocument>(
                    id,
                    new PartitionKey(id),
                    cancellationToken: cancellationToken);
                LogRequest("delete", CosmosContainerNames.Lists, response.StatusCode,
                    response.RequestCharge, stopwatch.Elapsed, retryCount: 0);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                LogRequest("delete", CosmosContainerNames.Lists, exception.StatusCode,
                    exception.RequestCharge, stopwatch.Elapsed, retryCount: 0);
            }
            catch (CosmosException exception)
            {
                LogRequest("delete", CosmosContainerNames.Lists, exception.StatusCode,
                    exception.RequestCharge, stopwatch.Elapsed, retryCount: 0);
                throw;
            }
        }

        public Task<CosmosItem<CosmosChannelDocument>> CreateChannelAsync(
            CosmosChannelDocument document,
            int retryCount,
            CancellationToken cancellationToken) => CreateAsync(
                _context.Channels,
                CosmosContainerNames.Channels,
                document,
                document.Id,
                retryCount,
                cancellationToken);

        public Task<CosmosItem<CosmosChannelDocument>> ReadChannelAsync(
            string id,
            int retryCount,
            CancellationToken cancellationToken) => ReadAsync<CosmosChannelDocument>(
                _context.Channels,
                CosmosContainerNames.Channels,
                id,
                retryCount,
                cancellationToken);

        public async Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken)
        {
            if (ids.Count == 0)
            {
                return Array.Empty<CosmosChannelDocument>();
            }

            var items = ids.Select(id => (id, new PartitionKey(id))).ToArray();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await _context.Channels.ReadManyItemsAsync<CosmosChannelDocument>(
                    items,
                    cancellationToken: cancellationToken);
                LogRequest("readMany", CosmosContainerNames.Channels, response.StatusCode,
                    response.RequestCharge, stopwatch.Elapsed, retryCount: 0);
                return response.ToArray();
            }
            catch (CosmosException exception)
            {
                LogRequest("readMany", CosmosContainerNames.Channels, exception.StatusCode,
                    exception.RequestCharge, stopwatch.Elapsed, retryCount: 0);
                throw;
            }
        }

        public Task<CosmosItem<CosmosChannelDocument>> ReplaceChannelAsync(
            CosmosChannelDocument document,
            string etag,
            int retryCount,
            CancellationToken cancellationToken) => ReplaceAsync(
                _context.Channels,
                CosmosContainerNames.Channels,
                document,
                document.Id,
                etag,
                retryCount,
                cancellationToken);

        private async Task<CosmosItem<T>> CreateAsync<T>(
            Container container,
            string containerName,
            T document,
            string id,
            int retryCount,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await container.CreateItemAsync(
                    document,
                    new PartitionKey(id),
                    cancellationToken: cancellationToken);
                LogRequest("create", containerName, response.StatusCode, response.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                return new CosmosItem<T>(response.Resource, response.ETag);
            }
            catch (CosmosException exception)
            {
                LogRequest("create", containerName, exception.StatusCode, exception.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                throw;
            }
        }

        private async Task<CosmosItem<T>> ReadAsync<T>(
            Container container,
            string containerName,
            string id,
            int retryCount,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await container.ReadItemAsync<T>(
                    id,
                    new PartitionKey(id),
                    cancellationToken: cancellationToken);
                LogRequest("pointRead", containerName, response.StatusCode, response.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                return new CosmosItem<T>(response.Resource, response.ETag);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                LogRequest("pointRead", containerName, exception.StatusCode, exception.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                return null;
            }
            catch (CosmosException exception)
            {
                LogRequest("pointRead", containerName, exception.StatusCode, exception.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                throw;
            }
        }

        private async Task<CosmosItem<T>> ReplaceAsync<T>(
            Container container,
            string containerName,
            T document,
            string id,
            string etag,
            int retryCount,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await container.ReplaceItemAsync(
                    document,
                    id,
                    new PartitionKey(id),
                    new ItemRequestOptions { IfMatchEtag = etag },
                    cancellationToken);
                LogRequest("replace", containerName, response.StatusCode, response.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                return new CosmosItem<T>(response.Resource, response.ETag);
            }
            catch (CosmosException exception)
            {
                LogRequest("replace", containerName, exception.StatusCode, exception.RequestCharge,
                    stopwatch.Elapsed, retryCount);
                throw;
            }
        }

        private void LogRequest(
            string operation,
            string container,
            HttpStatusCode status,
            double requestCharge,
            TimeSpan elapsed,
            int retryCount)
        {
            WriteTelemetry(
                _logger,
                operation,
                container,
                status,
                requestCharge,
                elapsed,
                retryCount);
        }

        internal static void WriteTelemetry(
            ILogger logger,
            string operation,
            string container,
            HttpStatusCode status,
            double requestCharge,
            TimeSpan elapsed,
            int retryCount)
        {
            logger.LogInformation(
                RequestEvent,
                "Cosmos request Operation={Operation} Container={Container} RequestCount={RequestCount} RequestCharge={RequestCharge} ElapsedMilliseconds={ElapsedMilliseconds} Status={Status} RetryCount={RetryCount}",
                operation,
                container,
                1,
                requestCharge,
                Math.Min(elapsed.TotalMilliseconds, int.MaxValue),
                (int)status,
                retryCount);
        }
    }
}
