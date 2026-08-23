using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly CosmosPersistenceContext _context;
        private readonly CosmosOperationExecutor _executor;

        public CosmosRepositoryClient(
            CosmosPersistenceContext context,
            ILogger logger)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _executor = new CosmosOperationExecutor(logger);
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
            await _executor.ExecuteItemAsync(
                "delete",
                CosmosContainerNames.Lists,
                retryCount: 0,
                token => _context.Lists.DeleteItemAsync<CosmosListDocument>(
                    id,
                    new PartitionKey(id),
                    cancellationToken: token),
                cancellationToken);
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
            var response = await _executor.ExecuteFeedPageAsync(
                "readMany",
                CosmosContainerNames.Channels,
                retryCount: 0,
                token => _context.Channels.ReadManyItemsAsync<CosmosChannelDocument>(
                    items,
                    cancellationToken: token),
                cancellationToken);
            return response.ToArray();
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
            var response = await _executor.ExecuteItemAsync(
                "create",
                containerName,
                retryCount,
                token => container.CreateItemAsync(
                    document,
                    new PartitionKey(id),
                    cancellationToken: token),
                cancellationToken);
            return new CosmosItem<T>(response.Resource, response.ETag);
        }

        private async Task<CosmosItem<T>> ReadAsync<T>(
            Container container,
            string containerName,
            string id,
            int retryCount,
            CancellationToken cancellationToken)
        {
            var response = await _executor.ExecuteItemAsync(
                "pointRead",
                containerName,
                retryCount,
                token => container.ReadItemAsync<T>(
                    id,
                    new PartitionKey(id),
                    cancellationToken: token),
                cancellationToken,
                returnNullOnNotFound: true);
            return response == null
                ? null
                : new CosmosItem<T>(response.Resource, response.ETag);
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
            var response = await _executor.ExecuteItemAsync(
                "replace",
                containerName,
                retryCount,
                token => container.ReplaceItemAsync(
                    document,
                    id,
                    new PartitionKey(id),
                    new ItemRequestOptions { IfMatchEtag = etag },
                    token),
                cancellationToken);
            return new CosmosItem<T>(response.Resource, response.ETag);
        }
    }
}
