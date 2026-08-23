using System;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosListRepository : IListRepository
    {
        private readonly ICosmosRepositoryClient _client;
        private readonly IAppClock _clock;
        private readonly ConditionalWeakTable<SubscriptionList, CosmosItem<CosmosListDocument>>
            _loadedItems = new();

        public CosmosListRepository(
            CosmosPersistenceContext context,
            IAppClock clock,
            ILogger<CosmosListRepository> logger)
            : this(new CosmosRepositoryClient(context, logger), clock)
        {
        }

        internal CosmosListRepository(ICosmosRepositoryClient client, IAppClock clock)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task CreateAsync(SubscriptionList list)
        {
            var document = CosmosDocumentMapper.ToDocument(list, _clock.UtcNow);
            await _client.CreateListAsync(document, retryCount: 0, CancellationToken.None);
        }

        public async Task<SubscriptionList> GetAsync(Guid id)
        {
            var item = await ReadAsync(id, retryCount: 0);
            if (item == null)
            {
                return null;
            }

            var list = CosmosDocumentMapper.ToSubscriptionList(item.Resource);
            _loadedItems.Add(list, item);
            return list;
        }

        public async Task<SubscriptionList> RenewExpirationAsync(
            SubscriptionList list,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn)
        {
            ArgumentNullException.ThrowIfNull(list);
            _loadedItems.TryGetValue(list, out var loadedItem);
            var renewedItem = await MutateAsync(
                list.Id,
                document =>
                {
                    if (document.ExpirationRenewedOn == renewedOn)
                    {
                        return false;
                    }

                    document.ExpiredAfter = expiredAfter;
                    document.ExpirationRenewedOn = renewedOn;
                    return true;
                },
                loadedItem);
            _loadedItems.Remove(list);
            var renewedList = CosmosDocumentMapper.ToSubscriptionList(renewedItem.Resource);
            _loadedItems.Add(renewedList, renewedItem);
            return renewedList;
        }

        public Task AddChannelAsync(Guid listId, string channelId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
            return MutateAsync(
                listId,
                document =>
                {
                    var channelIds = CosmosDocumentMapper.ToChannelIds(document).ToList();
                    if (channelIds.Contains(channelId, StringComparer.Ordinal))
                    {
                        return false;
                    }
                    if (channelIds.Count >= CosmosDocumentMapper.MaximumChannelIds)
                    {
                        throw new ListCapacityExceededException(
                            $"A list cannot contain more than {CosmosDocumentMapper.MaximumChannelIds} channels.");
                    }

                    channelIds.Add(channelId);
                    document.ChannelIds = channelIds;
                    return true;
                });
        }

        public Task RemoveChannelAsync(Guid listId, string channelId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
            return MutateAsync(
                listId,
                document =>
                {
                    var channelIds = CosmosDocumentMapper.ToChannelIds(document).ToList();
                    if (!channelIds.Remove(channelId))
                    {
                        return false;
                    }

                    document.ChannelIds = channelIds;
                    return true;
                });
        }

        public Task UpdateAsync(Guid id, string title, decimal playbackRate)
        {
            return MutateAsync(
                id,
                document =>
                {
                    document.Title = title;
                    document.PlaybackRate = playbackRate;
                    return true;
                });
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _client.DeleteListAsync(id.ToString("D"), CancellationToken.None);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                // List deletion is idempotent.
            }
        }

        private Task<CosmosItem<CosmosListDocument>> ReadAsync(Guid id, int retryCount)
        {
            return _client.ReadListAsync(
                id.ToString("D"),
                retryCount,
                CancellationToken.None);
        }

        private async Task<CosmosItem<CosmosListDocument>> MutateAsync(
            Guid id,
            Func<CosmosListDocument, bool> apply,
            CosmosItem<CosmosListDocument> initial = null)
        {
            var current = initial ?? await ReadAsync(id, retryCount: 0);
            if (current == null)
            {
                throw new InvalidOperationException("The list does not exist.");
            }

            for (var retryCount = 0; retryCount <= 1; retryCount++)
            {
                if (!apply(current.Resource))
                {
                    return current;
                }

                current.Resource.ChannelIds = CosmosDocumentMapper
                    .ToChannelIds(current.Resource);
                current.Resource.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    current.Resource.ExpiredAfter,
                    _clock.UtcNow);
                try
                {
                    return await _client.ReplaceListAsync(
                        current.Resource,
                        current.ETag,
                        retryCount,
                        CancellationToken.None);
                }
                catch (CosmosException exception) when (
                    retryCount == 0 && IsConcurrencyConflict(exception.StatusCode))
                {
                    current = await ReadAsync(id, retryCount: 1);
                    if (current == null)
                    {
                        throw new InvalidOperationException("The list no longer exists.");
                    }
                }
            }

            throw new InvalidOperationException("Unreachable list mutation state.");
        }

        private static bool IsConcurrencyConflict(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Conflict
                || statusCode == HttpStatusCode.PreconditionFailed;
        }
    }
}
