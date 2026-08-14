using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using youtubed.Domain;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosChannelRepository : IChannelRepository
    {
        private readonly ICosmosRepositoryClient _client;

        public CosmosChannelRepository(
            CosmosPersistenceContext context,
            ILogger<CosmosChannelRepository> logger)
            : this(new CosmosRepositoryClient(context, logger))
        {
        }

        internal CosmosChannelRepository(ICosmosRepositoryClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<Channel> GetByIdAsync(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var item = await _client.ReadChannelAsync(id, retryCount: 0, CancellationToken.None);
            return item == null ? null : CosmosDocumentMapper.ToChannel(item.Resource);
        }

        public async Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter)
        {
            ArgumentNullException.ThrowIfNull(channel);
            var discovered = CopyChannel(channel);
            discovered.StaleAfter = staleAfter;
            discovered.Status = ChannelStatus.Active;
            discovered.StatusReason = ChannelStatusReason.None;
            discovered.StatusUpdatedAt = null;

            var current = await _client.ReadChannelAsync(
                discovered.Id,
                retryCount: 0,
                CancellationToken.None);
            var firstRetryCount = 0;
            if (current == null)
            {
                try
                {
                    await _client.CreateChannelAsync(
                        CosmosDocumentMapper.ToDocument(discovered),
                        retryCount: 0,
                        CancellationToken.None);
                    return;
                }
                catch (CosmosException exception) when (IsConcurrencyConflict(exception.StatusCode))
                {
                    current = await _client.ReadChannelAsync(
                        discovered.Id,
                        retryCount: 1,
                        CancellationToken.None);
                    if (current == null)
                    {
                        throw;
                    }
                    firstRetryCount = 1;
                }
            }

            await ReplaceWithRetryAsync(
                current,
                document =>
                {
                    document.StaleAfter = staleAfter;
                    document.Status = ChannelStatus.Active.ToString();
                    document.StatusReason = null;
                    document.StatusUpdatedAt = null;
                },
                firstRetryCount,
                CancellationToken.None);
        }

        public async Task<IReadOnlyList<Channel>> GetBatchAsync(
            IReadOnlyCollection<string> channelIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(channelIds);
            var selectedIds = channelIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (selectedIds.Length > CosmosDocumentMapper.MaximumChannelIds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channelIds),
                    $"A Cosmos channel batch cannot exceed {CosmosDocumentMapper.MaximumChannelIds} IDs.");
            }
            if (selectedIds.Length == 0)
            {
                return Array.Empty<Channel>();
            }

            var documents = await _client.ReadChannelsAsync(selectedIds, cancellationToken);
            var channelsById = documents
                .ToDictionary(document => document.Id, StringComparer.Ordinal);
            return selectedIds
                .Where(channelsById.ContainsKey)
                .Select(id => CosmosDocumentMapper.ToChannel(channelsById[id]))
                .ToArray();
        }

        public async Task SaveRefreshResultsAsync(
            IReadOnlyCollection<ChannelRefreshResult> results,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(results);
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result?.Channel == null)
                {
                    throw new ArgumentException(
                        "Refresh results must contain a channel.",
                        nameof(results));
                }

                var replacement = CosmosDocumentMapper.ToDocument(result.Channel);
                var current = await _client.ReadChannelAsync(
                    replacement.Id,
                    retryCount: 0,
                    cancellationToken);
                if (current == null)
                {
                    throw new InvalidOperationException("The refreshed channel does not exist.");
                }

                await ReplaceWithRetryAsync(
                    current,
                    document =>
                    {
                        var retainedVideos = document.Videos;
                        document.Url = replacement.Url;
                        document.Title = replacement.Title;
                        document.Thumbnail = replacement.Thumbnail;
                        document.PlaylistId = replacement.PlaylistId;
                        document.StaleAfter = replacement.StaleAfter;
                        document.Status = replacement.Status;
                        document.StatusReason = replacement.StatusReason;
                        document.StatusUpdatedAt = replacement.StatusUpdatedAt;
                        document.Videos = result.VideosRefreshed
                            ? replacement.Videos
                            : retainedVideos;
                    },
                    firstRetryCount: 0,
                    cancellationToken);
            }
        }

        public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now)
        {
            return Task.FromResult(0);
        }

        private async Task ReplaceWithRetryAsync(
            CosmosItem<CosmosChannelDocument> initial,
            Action<CosmosChannelDocument> apply,
            int firstRetryCount,
            CancellationToken cancellationToken)
        {
            var current = initial;
            for (var retryCount = firstRetryCount; retryCount <= 1; retryCount++)
            {
                apply(current.Resource);
                try
                {
                    await _client.ReplaceChannelAsync(
                        current.Resource,
                        current.ETag,
                        retryCount,
                        cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    retryCount == 0 && IsConcurrencyConflict(exception.StatusCode))
                {
                    current = await _client.ReadChannelAsync(
                        current.Resource.Id,
                        retryCount: 1,
                        cancellationToken);
                    if (current == null)
                    {
                        throw new InvalidOperationException("The channel no longer exists.");
                    }
                }
            }
        }

        private static Channel CopyChannel(Channel channel)
        {
            return new Channel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = channel.Videos?.ToArray() ?? Array.Empty<ChannelVideo>()
            };
        }

        private static bool IsConcurrencyConflict(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Conflict
                || statusCode == HttpStatusCode.PreconditionFailed;
        }
    }
}
