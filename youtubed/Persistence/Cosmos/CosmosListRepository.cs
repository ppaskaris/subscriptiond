using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using youtubed.Domain;
using youtubed.SecurityTheatre;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosListRepository : IListRepository
    {
        private readonly ICosmosRepositoryClient _client;
        private readonly IAppClock _clock;

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
            var document = CosmosDocumentMapper.ToDocument(
                list,
                Array.Empty<string>(),
                _clock.UtcNow);
            await _client.CreateListAsync(document, retryCount: 0, CancellationToken.None);
        }

        public async Task<SubscriptionList> GetAsync(Guid id)
        {
            var item = await ReadAsync(id, retryCount: 0);
            return item == null
                ? null
                : CosmosDocumentMapper.ToSubscriptionList(item.Resource);
        }

        public async Task<ListVideoProjection> GetAuthenticatedVideoProjectionAsync(
            Guid id,
            byte[] token,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn,
            int videoLimit)
        {
            var item = await ReadAsync(id, retryCount: 0);
            if (item == null || TokenUtils.NotEqual(token, item.Resource.Token))
            {
                return null;
            }

            if (item.Resource.ExpirationRenewedOn != renewedOn)
            {
                item = await MutateAsync(
                    id,
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
                    item);
            }

            return await CreateVideoProjectionAsync(item.Resource, videoLimit);
        }

        public Task RenewExpirationAsync(
            Guid id,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn)
        {
            return MutateAsync(
                id,
                document =>
                {
                    if (document.ExpirationRenewedOn == renewedOn)
                    {
                        return false;
                    }

                    document.ExpiredAfter = expiredAfter;
                    document.ExpirationRenewedOn = renewedOn;
                    return true;
                });
        }

        public async Task<ListVideoProjection> GetVideoProjectionAsync(
            SubscriptionList list,
            int videoLimit)
        {
            if (list == null)
            {
                return null;
            }

            var item = await ReadAsync(list.Id, retryCount: 0);
            return item == null
                ? null
                : await CreateVideoProjectionAsync(item.Resource, videoLimit);
        }

        public async Task<ListChannelProjection> GetChannelProjectionAsync(SubscriptionList list)
        {
            if (list == null)
            {
                return null;
            }

            var item = await ReadAsync(list.Id, retryCount: 0);
            if (item == null)
            {
                return null;
            }

            var document = item.Resource;
            var channelIds = CosmosDocumentMapper.ToChannelIds(document);
            var channelDocuments = await ReadChannelsAsync(channelIds);
            var channelsById = channelDocuments.ToDictionary(
                channel => channel.Id,
                StringComparer.Ordinal);
            return new ListChannelProjection
            {
                List = CosmosDocumentMapper.ToSubscriptionList(document),
                ChannelIds = channelIds,
                Channels = channelIds
                    .Select(channelId => channelsById.TryGetValue(channelId, out var channel)
                        ? ToChannelProjection(channel)
                        : CreateMissingChannelProjection(channelId))
                    .OrderBy(channel => channel.Title, StringComparer.Ordinal)
                    .ThenBy(channel => channel.Id, StringComparer.Ordinal)
                    .ToArray()
            };
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
                    document.ChannelIds = channelIds
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
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

        public Task DeleteAsync(Guid id)
        {
            return _client.DeleteListAsync(id.ToString("D"), CancellationToken.None);
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

        private async Task<ListVideoProjection> CreateVideoProjectionAsync(
            CosmosListDocument document,
            int videoLimit)
        {
            var channelIds = CosmosDocumentMapper.ToChannelIds(document);
            var channelDocuments = await ReadChannelsAsync(channelIds);
            var selectedVideos = channelDocuments
                .SelectMany(channel => (channel.Videos ?? Array.Empty<CosmosVideoDocument>())
                    .Select(video => (ChannelId: channel.Id, Video: video)))
                .OrderByDescending(value => value.Video.PublishedAt)
                .ThenBy(value => value.Video.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, videoLimit))
                .ToLookup(value => value.ChannelId, value => value.Video, StringComparer.Ordinal);

            return new ListVideoProjection
            {
                List = CosmosDocumentMapper.ToSubscriptionList(document),
                ChannelIds = channelIds,
                Channels = channelDocuments
                    .OrderBy(channel => channel.Id, StringComparer.Ordinal)
                    .Select(channel => ToVideoProjection(channel, selectedVideos[channel.Id]))
                    .ToArray()
            };
        }

        private static ListChannelProjection.Channel ToChannelProjection(
            CosmosChannelDocument document)
        {
            var channel = CosmosDocumentMapper.ToChannel(document);
            return new ListChannelProjection.Channel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt
            };
        }

        private static ListChannelProjection.Channel CreateMissingChannelProjection(string channelId)
        {
            return new ListChannelProjection.Channel
            {
                Id = channelId,
                Url = string.Format(Constants.YoutubeChannelUrl, channelId),
                Title = "Temporarily unavailable",
                Status = ChannelStatus.Unavailable,
                StatusReason = ChannelStatusReason.None,
                IsMissing = true
            };
        }

        private static ListVideoProjection.Channel ToVideoProjection(
            CosmosChannelDocument document,
            IEnumerable<CosmosVideoDocument> selectedVideos)
        {
            var channel = CosmosDocumentMapper.ToChannel(document);
            var selectedIds = selectedVideos
                .Select(video => video.Id)
                .ToHashSet(StringComparer.Ordinal);
            return new ListVideoProjection.Channel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = channel.Videos
                    .Where(video => selectedIds.Contains(video.VideoId))
                    .OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .ToArray()
            };
        }

        private static bool IsConcurrencyConflict(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Conflict
                || statusCode == HttpStatusCode.PreconditionFailed;
        }

        private Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
            IReadOnlyCollection<string> channelIds)
        {
            return channelIds.Count == 0
                ? Task.FromResult<IReadOnlyList<CosmosChannelDocument>>(
                    Array.Empty<CosmosChannelDocument>())
                : _client.ReadChannelsAsync(channelIds, CancellationToken.None);
        }
    }
}
