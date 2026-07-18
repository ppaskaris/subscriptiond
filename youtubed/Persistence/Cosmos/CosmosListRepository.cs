using Microsoft.Azure.Cosmos;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosListRepository : IListRepository
    {
        private const int MaxWriteAttempts = 2;

        private readonly Container _lists;
        private readonly Container _channels;
        private readonly IAppClock _clock;

        public CosmosListRepository(Container lists, Container channels, IAppClock clock)
        {
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task CreateAsync(ListModel list)
        {
            var document = CosmosDocumentMapper.ToDocument(
                new SubscriptionList
                {
                    Id = list.Id,
                    Token = list.Token,
                    Title = list.Title,
                    PlaybackRate = list.PlaybackRate,
                    ExpiredAfter = list.ExpiredAfter,
                    ExpirationRenewedOn = list.ExpirationRenewedOn
                },
                _clock.UtcNow);

            await _lists.CreateItemAsync(document, new PartitionKey(document.Id));
        }

        public async Task<ListModel> GetAsync(Guid id)
        {
            var document = await ReadListAsync(id);
            if (document == null)
            {
                return null;
            }

            var list = CosmosDocumentMapper.ToSubscriptionList(document);
            return new ListModel
            {
                Id = list.Id,
                Token = list.Token,
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                ExpirationRenewedOn = list.ExpirationRenewedOn
            };
        }

        public async Task RenewExpirationAsync(
            Guid id,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(id);
                if (document == null || document.ExpirationRenewedOn == renewedOn)
                {
                    return;
                }

                document.ExpiredAfter = expiredAfter;
                document.ExpirationRenewedOn = renewedOn;
                document.Ttl = CosmosDocumentMapper.GetTtlSeconds(expiredAfter, _clock.UtcNow);

                try
                {
                    await ReplaceAsync(document);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }
        }

        public async Task<ListVideoProjection> GetVideoProjectionAsync(
            SubscriptionList list,
            int videoLimit)
        {
            var document = await ReadListAsync(list.Id);
            return document == null
                ? null
                : CosmosDocumentMapper.ToVideoProjection(document, videoLimit);
        }

        public async Task<ListChannelProjection> GetChannelProjectionAsync(SubscriptionList list)
        {
            var document = await ReadListAsync(list.Id);
            return document == null
                ? null
                : CosmosDocumentMapper.ToChannelProjection(document);
        }

        public async Task AddChannelAsync(Guid listId, string channelId)
        {
            var channelResponse = await _channels.ReadItemAsync<CosmosChannelDocument>(
                channelId,
                new PartitionKey(channelId));
            var projectedChannel = CosmosDocumentMapper.ToProjectedChannelDocument(
                CosmosDocumentMapper.ToChannel(channelResponse.Resource));

            await UpdateMembershipAsync(
                listId,
                document =>
                {
                    if (document.Channels.Any(channel =>
                        string.Equals(channel.Id, channelId, StringComparison.Ordinal)))
                    {
                        return false;
                    }

                    document.Channels = document.Channels.Append(projectedChannel).ToArray();
                    return true;
                });
        }

        public Task RemoveChannelAsync(Guid listId, string channelId)
        {
            return UpdateMembershipAsync(
                listId,
                document =>
                {
                    var channels = document.Channels
                        .Where(channel => !string.Equals(
                            channel.Id,
                            channelId,
                            StringComparison.Ordinal))
                        .ToArray();
                    if (channels.Length == document.Channels.Count)
                    {
                        return false;
                    }

                    document.Channels = channels;
                    return true;
                });
        }

        public Task UpdateAsync(Guid id, string title, decimal playbackRate)
        {
            return UpdateListAsync(
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
            var documentId = id.ToString("D");
            try
            {
                await _lists.DeleteItemAsync<CosmosListDocument>(
                    documentId,
                    new PartitionKey(documentId));
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        public Task<int> RemoveExpiredAsync(DateTimeOffset now)
        {
            return Task.FromResult(0);
        }

        private Task UpdateMembershipAsync(
            Guid listId,
            Func<CosmosListDocument, bool> update)
        {
            return UpdateListAsync(listId, update);
        }

        private async Task UpdateListAsync(
            Guid id,
            Func<CosmosListDocument, bool> update)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(id);
                if (document == null || !update(document))
                {
                    return;
                }

                try
                {
                    await ReplaceAsync(document);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }
        }

        private async Task<CosmosListDocument> ReadListAsync(Guid id)
        {
            var documentId = id.ToString("D");
            try
            {
                var response = await _lists.ReadItemAsync<CosmosListDocument>(
                    documentId,
                    new PartitionKey(documentId));
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private Task<ItemResponse<CosmosListDocument>> ReplaceAsync(CosmosListDocument document)
        {
            document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                document.ExpiredAfter,
                _clock.UtcNow);

            return _lists.ReplaceItemAsync(
                document,
                document.Id,
                new PartitionKey(document.Id),
                new ItemRequestOptions { IfMatchEtag = document.ETag });
        }
    }
}
