using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosChannelRepository : IChannelRepository
    {
        private const int MaxWriteAttempts = 2;
        private const int MaxCanonicalVideos = 100;

        private readonly Container _channels;
        private readonly Container _lists;
        private readonly IAppClock _clock;

        public CosmosChannelRepository(Container channels, Container lists, IAppClock clock)
            : this(channels, lists, null, clock, null)
        {
        }

        internal CosmosChannelRepository(
            Container channels,
            Container lists,
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions recoveryOptions)
        {
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<ChannelModel> GetByIdAsync(string id)
        {
            var document = await ReadChannelAsync(id, CancellationToken.None);
            if (document == null)
            {
                return null;
            }

            var channel = CosmosDocumentMapper.ToChannel(document);
            return new ChannelModel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt
            };
        }

        public async Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadChannelAsync(channel.Id, CancellationToken.None);
                if (document == null)
                {
                    var discovered = CosmosDocumentMapper.ToChannelDocument(
                        new Channel
                        {
                            Id = channel.Id,
                            Url = channel.Url,
                            Title = channel.Title,
                            Thumbnail = channel.Thumbnail,
                            PlaylistId = channel.PlaylistId,
                            StaleAfter = staleAfter,
                            Status = ChannelStatus.Active,
                            StatusReason = ChannelStatusReason.None,
                            OrphanedAfter = _clock.UtcNow
                        },
                        _clock.UtcNow,
                        Constants.ChannelOrphanRetention);

                    try
                    {
                        await _channels.CreateItemAsync(discovered, new PartitionKey(discovered.Id));
                        return;
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.Conflict
                        && attempt + 1 < MaxWriteAttempts)
                    {
                        CosmosRecoveryTelemetry.RecordConflict(
                            "ChannelRepository",
                            "CreateCanonical",
                            retry: true);
                        continue;
                    }
                }

                document.StaleAfter = staleAfter;
                document.Status = ChannelStatus.Active.ToString();
                document.StatusReason = ChannelStatusReason.None.ToString();
                document.StatusUpdatedAt = null;
                try
                {
                    await ReplaceAsync(document, CancellationToken.None);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ChannelRepository",
                        "UpdateCanonical",
                        retry: true);
                }
            }
        }

        public Task UpdateMetadataAsync(
            string id,
            string url,
            string title,
            string thumbnail,
            string playlistId)
        {
            return UpdateChannelAsync(
                id,
                document =>
                {
                    document.Url = url;
                    document.Title = title;
                    document.Thumbnail = thumbnail;
                    document.PlaylistId = playlistId;
                    document.Status = ChannelStatus.Active.ToString();
                    document.StatusReason = ChannelStatusReason.None.ToString();
                    document.StatusUpdatedAt = null;
                },
                CancellationToken.None);
        }

        public Task MarkUnavailableAsync(
            string id,
            ChannelStatusReason reason,
            DateTimeOffset statusUpdatedAt,
            DateTimeOffset staleAfter)
        {
            return UpdateChannelAsync(
                id,
                document =>
                {
                    document.Status = ChannelStatus.Unavailable.ToString();
                    document.StatusReason = reason.ToString();
                    document.StatusUpdatedAt = statusUpdatedAt;
                    document.StaleAfter = staleAfter;
                },
                CancellationToken.None);
        }

        public async Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
            DateTimeOffset now,
            int take,
            CancellationToken cancellationToken)
        {
            if (take <= 0)
            {
                return Array.Empty<StaleChannelReference>();
            }

            var query = new QueryDefinition(
                    "SELECT TOP @take c.id, c.staleAfter FROM c " +
                    "WHERE c.staleAfter <= @now AND c.subscriptionCount > 0 AND c.status = @status " +
                    "ORDER BY c.staleAfter ASC, c.id ASC")
                .WithParameter("@take", take)
                .WithParameter("@now", now)
                .WithParameter("@status", ChannelStatus.Active.ToString());
            var results = new List<StaleChannelReference>();
            using var iterator = _channels.GetItemQueryIterator<StaleChannelReference>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = take });
            while (iterator.HasMoreResults && results.Count < take)
            {
                results.AddRange(await iterator.ReadNextAsync(cancellationToken));
            }

            return results.Take(take).ToArray();
        }

        public async Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
            CancellationToken cancellationToken)
        {
            var query = new QueryDefinition(
                    "SELECT TOP 1 VALUE c.staleAfter FROM c " +
                    "WHERE c.subscriptionCount > 0 AND c.status = @status " +
                    "ORDER BY c.staleAfter ASC, c.id ASC")
                .WithParameter("@status", ChannelStatus.Active.ToString());
            using var iterator = _channels.GetItemQueryIterator<DateTimeOffset>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
            if (!iterator.HasMoreResults)
            {
                return null;
            }

            var response = await iterator.ReadNextAsync(cancellationToken);
            return response.Count == 0 ? null : response.First();
        }

        public async Task<IReadOnlyList<Channel>> GetBatchAsync(
            IReadOnlyCollection<string> channelIds,
            CancellationToken cancellationToken)
        {
            var channels = new List<Channel>();
            foreach (var channelId in channelIds.Distinct(StringComparer.Ordinal))
            {
                var document = await ReadChannelAsync(channelId, cancellationToken);
                if (document == null)
                {
                    continue;
                }

                document = await RepairReferencesAsync(document, cancellationToken);
                if (document != null)
                {
                    var channel = CosmosDocumentMapper.ToChannel(document);
                    if (channel.SubscriptionCount > 0
                        && channel.Status == ChannelStatus.Active
                        && channel.StaleAfter <= _clock.UtcNow)
                    {
                        channels.Add(channel);
                    }
                }
            }

            return channels;
        }

        public async Task SaveRefreshResultsAsync(
            IReadOnlyCollection<ChannelRefreshResult> results,
            CancellationToken cancellationToken)
        {
            foreach (var result in results)
            {
                await UpdateChannelAsync(
                    result.Channel.Id,
                    document =>
                    {
                        ApplyRefreshResult(document, result);
                        document.ProjectionVersion++;
                        document.ProjectionRecoveryPending = true;
                        document.ProjectionRecoveryDueAt = _clock.UtcNow;
                        document.ProjectionRecoveryStartedAt ??= _clock.UtcNow;
                        document.ProjectionRecoveryAttempt = 0;
                        document.ProjectionRecoveryPoison = false;
                        document.ProjectionRecoveryLastErrorClass = null;
                        document.ProjectionRecoveryProjectionVersion = null;
                        document.ProjectionRecoverySubscriptionGeneration = null;
                        document.ProjectionRecoveryAfterListId = null;
                    },
                    cancellationToken);
            }
        }

        public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now)
        {
            return Task.FromResult(0);
        }

        public async Task UpdateSubscriptionAsync(
            string channelId,
            Guid listId,
            CancellationToken cancellationToken = default)
        {
            await UpdateChannelAsync(
                channelId,
                async document =>
                {
                    var validListIds = await GetValidListIdsAsync(
                        document.SubscribedListIds.Append(listId.ToString("D")),
                        channelId,
                        cancellationToken);
                    ApplySubscriptions(document, validListIds);
                },
                cancellationToken);
        }

        internal async Task<bool> ReserveSubscriptionAsync(
            string channelId,
            Guid listId,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadChannelAsync(channelId, cancellationToken);
                if (document == null)
                {
                    return false;
                }

                var ids = document.SubscribedListIds
                    .Append(listId.ToString("D"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                ApplySubscriptions(document, ids.Select(Guid.Parse).ToArray());
                try
                {
                    await ReplaceAsync(document, cancellationToken);
                    return true;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ChannelRepository",
                        "ReserveSubscription",
                        retry: true);
                }
            }

            return false;
        }

        internal async Task<bool> RepairSubscriptionFromListTruthAsync(
            string channelId,
            Guid listId,
            CancellationToken cancellationToken)
        {
            var list = await ReadListAsync(listId, cancellationToken);
            var isMember = list?.Channels.Any(channel => string.Equals(
                channel.Id,
                channelId,
                StringComparison.Ordinal)) == true;
            var changed = false;
            await UpdateChannelAsync(
                channelId,
                document =>
                {
                    var normalized = document.SubscribedListIds
                        .Where(value => Guid.TryParse(value, out _))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (isMember)
                    {
                        changed = normalized.Add(listId.ToString("D"));
                    }
                    else
                    {
                        changed = normalized.Remove(listId.ToString("D"));
                    }

                    var ids = normalized
                        .Select(Guid.Parse)
                        .OrderBy(id => id)
                        .ToArray();
                    if (!changed && ReferencesMatch(document, ids))
                    {
                        return;
                    }

                    ApplySubscriptions(document, ids);
                },
                cancellationToken);
            return changed;
        }

        private async Task<CosmosChannelDocument> RepairReferencesAsync(
            CosmosChannelDocument document,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var validListIds = await GetValidListIdsAsync(
                    document.SubscribedListIds,
                    document.Id,
                    cancellationToken);
                if (ReferencesMatch(document, validListIds))
                {
                    return document;
                }

                ApplySubscriptions(document, validListIds);
                try
                {
                    var response = await ReplaceAsync(document, cancellationToken);
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ChannelRepository",
                        "RepairSubscription",
                        retry: true);
                    document = await ReadChannelAsync(document.Id, cancellationToken);
                    if (document == null)
                    {
                        return null;
                    }
                }
            }

            return document;
        }

        private async Task<IReadOnlyList<Guid>> GetValidListIdsAsync(
            IEnumerable<string> candidateIds,
            string channelId,
            CancellationToken cancellationToken)
        {
            var validIds = new List<Guid>();
            foreach (var candidateId in candidateIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(candidateId, out var listId))
                {
                    continue;
                }

                var list = await ReadListAsync(listId, cancellationToken);
                if (list?.Channels.Any(channel => string.Equals(
                    channel.Id,
                    channelId,
                    StringComparison.Ordinal)) == true)
                {
                    validIds.Add(listId);
                }
            }

            return validIds.OrderBy(id => id).ToArray();
        }

        private async Task<CosmosListDocument> ReadListAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            var documentId = id.ToString("D");
            try
            {
                var response = await _lists.ReadItemAsync<CosmosListDocument>(
                    documentId,
                    new PartitionKey(documentId),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task<CosmosChannelDocument> ReadChannelAsync(
            string id,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                    id,
                    new PartitionKey(id),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task UpdateChannelAsync(
            string id,
            Action<CosmosChannelDocument> update,
            CancellationToken cancellationToken)
        {
            await UpdateChannelAsync(
                id,
                document =>
                {
                    update(document);
                    return Task.CompletedTask;
                },
                cancellationToken);
        }

        private async Task UpdateChannelAsync(
            string id,
            Func<CosmosChannelDocument, Task> update,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadChannelAsync(id, cancellationToken);
                if (document == null)
                {
                    return;
                }

                await update(document);
                try
                {
                    await ReplaceAsync(document, cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ChannelRepository",
                        "RemoveSubscription",
                        retry: true);
                }
            }
        }

        private Task<ItemResponse<CosmosChannelDocument>> ReplaceAsync(
            CosmosChannelDocument document,
            CancellationToken cancellationToken)
        {
            document.Ttl = document.SubscriptionCount == 0 && document.OrphanedAfter.HasValue
                ? CosmosDocumentMapper.GetTtlSeconds(
                    document.OrphanedAfter.Value + Constants.ChannelOrphanRetention,
                    _clock.UtcNow)
                : -1;
            using (var stream = CosmosSystemTextJsonSerializer.Instance.ToStream(document))
            {
                if (stream.Length >= Constants.CosmosChannelSerializedSizeSafetyCeilingBytes)
                {
                    throw new ListCapacityExceededException(
                        $"The canonical channel exceeds the {Constants.CosmosChannelSerializedSizeSafetyCeilingBytes}-byte safety ceiling.");
                }
            }

            return _channels.ReplaceItemAsync(
                document,
                document.Id,
                new PartitionKey(document.Id),
                new ItemRequestOptions { IfMatchEtag = document.ETag },
                cancellationToken);
        }

        private void ApplySubscriptions(
            CosmosChannelDocument document,
            IReadOnlyList<Guid> validListIds)
        {
            var previous = document.SubscribedListIds
                .Where(value => Guid.TryParse(value, out _))
                .Select(Guid.Parse)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var normalized = validListIds.Distinct().OrderBy(id => id).ToArray();
            document.SubscribedListIds = normalized.Select(id => id.ToString("D")).ToArray();
            document.SubscriptionCount = normalized.Length;
            if (!previous.SequenceEqual(normalized))
            {
                document.SubscriptionGeneration++;
                if (document.ProjectionRecoveryPending)
                {
                    document.ProjectionRecoveryDueAt = _clock.UtcNow;
                    document.ProjectionRecoveryProjectionVersion = null;
                    document.ProjectionRecoverySubscriptionGeneration = null;
                    document.ProjectionRecoveryAfterListId = null;
                }
            }
            if (validListIds.Count == 0)
            {
                document.OrphanedAfter ??= _clock.UtcNow;
            }
            else
            {
                document.OrphanedAfter = null;
            }
        }

        private static bool ReferencesMatch(
            CosmosChannelDocument document,
            IReadOnlyList<Guid> validListIds)
        {
            var storedIds = document.SubscribedListIds
                .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .OrderBy(id => id)
                .ToArray();
            return document.SubscriptionCount == validListIds.Count
                && storedIds.SequenceEqual(validListIds)
                && storedIds.Length == document.SubscribedListIds.Count;
        }

        private static void ApplyRefreshResult(
            CosmosChannelDocument document,
            ChannelRefreshResult result)
        {
            var channel = result.Channel;
            document.Url = channel.Url;
            document.Title = channel.Title;
            document.Thumbnail = channel.Thumbnail;
            document.PlaylistId = channel.PlaylistId;
            document.StaleAfter = channel.StaleAfter;
            document.Status = channel.Status.ToString();
            document.StatusReason = channel.StatusReason.ToString();
            document.StatusUpdatedAt = channel.StatusUpdatedAt;
            if (result.VideosRefreshed)
            {
                document.Videos = channel.Videos
                    .OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .Take(MaxCanonicalVideos)
                    .Select(video => new CosmosVideoDocument
                    {
                        Id = video.VideoId,
                        Title = video.Title,
                        DurationTicks = video.Duration.Ticks,
                        PublishedAt = video.PublishedAt,
                        Thumbnail = video.ThumbnailUrl
                    })
                    .ToArray();
            }
        }
    }
}
