using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
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
        private readonly CosmosChannelRepository _channelRepository;

        public CosmosListRepository(Container lists, Container channels, IAppClock clock)
        {
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _channelRepository = new CosmosChannelRepository(_channels, _lists, _clock);
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
            await _channelRepository.UpdateSubscriptionAsync(channelId, listId);
        }

        public async Task RemoveChannelAsync(Guid listId, string channelId)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(listId);
                if (document == null)
                {
                    break;
                }

                var remainingChannels = document.Channels
                    .Where(channel => !string.Equals(
                        channel.Id,
                        channelId,
                        StringComparison.Ordinal))
                    .ToArray();
                if (remainingChannels.Length == document.Channels.Count)
                {
                    break;
                }

                try
                {
                    document.Channels =
                        await RehydrateUnderfilledChannelsAsync(remainingChannels);
                    try
                    {
                        await ReplaceAsync(document);
                    }
                    catch (ListCapacityExceededException)
                    {
                        document.Channels = remainingChannels;
                        await ReplaceAsync(document);
                    }

                    break;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }

            await _channelRepository.UpdateSubscriptionAsync(channelId, listId);
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
            CosmosListDocument deletedDocument = null;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(id);
                if (document == null)
                {
                    return;
                }

                try
                {
                    await _lists.DeleteItemAsync<CosmosListDocument>(
                        documentId,
                        new PartitionKey(documentId),
                        new ItemRequestOptions { IfMatchEtag = document.ETag });
                    deletedDocument = document;
                    break;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
            }

            if (deletedDocument != null)
            {
                foreach (var channelId in deletedDocument.Channels
                    .Select(channel => channel.Id)
                    .Distinct(StringComparer.Ordinal))
                {
                    await _channelRepository.UpdateSubscriptionAsync(channelId, id);
                }
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

        private async Task<IReadOnlyList<CosmosProjectedChannelDocument>>
            RehydrateUnderfilledChannelsAsync(
                IReadOnlyCollection<CosmosProjectedChannelDocument> channels)
        {
            var targetVideoCount =
                CosmosListProjectionPolicy.GetTargetVideoCountPerChannel(channels.Count);
            var hydratedChannels = new CosmosProjectedChannelDocument[channels.Count];
            var index = 0;
            foreach (var projectedChannel in channels)
            {
                var distinctVideoCount = (projectedChannel.Videos
                        ?? Array.Empty<CosmosVideoDocument>())
                    .Where(video => video != null)
                    .Select(video => video.Id)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (distinctVideoCount >= targetVideoCount)
                {
                    hydratedChannels[index++] = projectedChannel;
                    continue;
                }

                var canonicalChannel = await ReadChannelAsync(projectedChannel.Id);
                hydratedChannels[index++] = canonicalChannel == null
                    ? projectedChannel
                    : CosmosDocumentMapper.ToProjectedChannelDocument(
                        CosmosDocumentMapper.ToChannel(canonicalChannel));
            }

            return hydratedChannels;
        }

        private async Task<CosmosChannelDocument> ReadChannelAsync(string id)
        {
            try
            {
                var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                    id,
                    new PartitionKey(id));
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
            var boundedDocument = CosmosListProjectionPolicy.CreateBoundedCopy(
                document,
                _clock.UtcNow);

            return _lists.ReplaceItemAsync(
                boundedDocument,
                boundedDocument.Id,
                new PartitionKey(boundedDocument.Id),
                new ItemRequestOptions { IfMatchEtag = boundedDocument.ETag });
        }
    }
}
