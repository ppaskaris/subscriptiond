using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosListProjectionRepository : IListProjectionRepository
    {
        private const int MaxWriteAttempts = 2;

        private readonly Container _lists;
        private readonly Container _channels;
        private readonly IAppClock _clock;

        public CosmosListProjectionRepository(
            Container lists,
            Container channels,
            IAppClock clock)
        {
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task UpdateProjectedChannelsAsync(
            IReadOnlyCollection<Channel> refreshedChannels,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(refreshedChannels);

            var projectedChannels = refreshedChannels
                .GroupBy(channel => channel.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToDictionary(
                    channel => channel.Id,
                    CosmosDocumentMapper.ToProjectedChannelDocument,
                    StringComparer.Ordinal);
            var channelsByList = refreshedChannels
                .GroupBy(channel => channel.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .SelectMany(channel => channel.SubscribedListIds.Select(listId => new
                {
                    ListId = listId,
                    ChannelId = channel.Id
                }))
                .GroupBy(reference => reference.ListId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(reference => reference.ChannelId)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            var deadReferences = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

            foreach (var listChannels in channelsByList)
            {
                await UpdateListAsync(
                    listChannels.Key,
                    listChannels.Value,
                    projectedChannels,
                    deadReferences,
                    cancellationToken);
            }

            foreach (var deadReference in deadReferences)
            {
                await RepairChannelReferencesAsync(
                    deadReference.Key,
                    deadReference.Value,
                    cancellationToken);
            }
        }

        private async Task UpdateListAsync(
            Guid listId,
            IReadOnlyCollection<string> channelIds,
            IReadOnlyDictionary<string, CosmosProjectedChannelDocument> projectedChannels,
            IDictionary<string, HashSet<Guid>> deadReferences,
            CancellationToken cancellationToken)
        {
            var channelIdSet = channelIds.ToHashSet(StringComparer.Ordinal);
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(listId, cancellationToken);
                if (document == null)
                {
                    AddDeadReferences(deadReferences, channelIds, listId);
                    return;
                }

                var replacedChannelIds = new HashSet<string>(StringComparer.Ordinal);
                document.Channels = document.Channels
                    .Select(channel => projectedChannels.TryGetValue(channel.Id, out var projected)
                        && channelIdSet.Contains(channel.Id)
                            ? RecordReplacement(projected, replacedChannelIds)
                            : channel)
                    .ToArray();

                var missingChannelIds = channelIds
                    .Where(channelId => !replacedChannelIds.Contains(channelId))
                    .ToArray();
                if (replacedChannelIds.Count == 0)
                {
                    AddDeadReferences(deadReferences, missingChannelIds, listId);
                    return;
                }

                document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    document.ExpiredAfter,
                    _clock.UtcNow);

                try
                {
                    await _lists.ReplaceItemAsync(
                        document,
                        document.Id,
                        new PartitionKey(document.Id),
                        new ItemRequestOptions { IfMatchEtag = document.ETag },
                        cancellationToken);
                    AddDeadReferences(deadReferences, missingChannelIds, listId);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    AddDeadReferences(deadReferences, channelIds, listId);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }
        }

        private async Task RepairChannelReferencesAsync(
            string channelId,
            IReadOnlySet<Guid> deadListIds,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadChannelAsync(channelId, cancellationToken);
                if (document == null)
                {
                    return;
                }

                var confirmedDeadListIds = await GetConfirmedDeadListIdsAsync(
                    channelId,
                    deadListIds,
                    cancellationToken);
                var subscriptions = document.SubscribedListIds
                    .Where(value => Guid.TryParse(value, out var listId)
                        && !confirmedDeadListIds.Contains(listId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (subscriptions.Length == document.SubscribedListIds.Count)
                {
                    return;
                }

                document.SubscribedListIds = subscriptions;
                document.SubscriptionCount = subscriptions.Length;
                if (subscriptions.Length == 0)
                {
                    document.OrphanedAfter ??= _clock.UtcNow;
                    document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                        document.OrphanedAfter.Value + Constants.ChannelOrphanRetention,
                        _clock.UtcNow);
                }
                else
                {
                    document.OrphanedAfter = null;
                    document.Ttl = -1;
                }

                try
                {
                    await _channels.ReplaceItemAsync(
                        document,
                        document.Id,
                        new PartitionKey(document.Id),
                        new ItemRequestOptions { IfMatchEtag = document.ETag },
                        cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                }
            }
        }

        private async Task<IReadOnlySet<Guid>> GetConfirmedDeadListIdsAsync(
            string channelId,
            IReadOnlySet<Guid> candidateListIds,
            CancellationToken cancellationToken)
        {
            var confirmedDeadListIds = new HashSet<Guid>();
            foreach (var listId in candidateListIds)
            {
                var list = await ReadListAsync(listId, cancellationToken);
                if (list == null || !list.Channels.Any(channel => string.Equals(
                    channel.Id,
                    channelId,
                    StringComparison.Ordinal)))
                {
                    confirmedDeadListIds.Add(listId);
                }
            }

            return confirmedDeadListIds;
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

        private static CosmosProjectedChannelDocument RecordReplacement(
            CosmosProjectedChannelDocument projected,
            ISet<string> replacedChannelIds)
        {
            replacedChannelIds.Add(projected.Id);
            return projected;
        }

        private static void AddDeadReferences(
            IDictionary<string, HashSet<Guid>> deadReferences,
            IEnumerable<string> channelIds,
            Guid listId)
        {
            foreach (var channelId in channelIds)
            {
                if (!deadReferences.TryGetValue(channelId, out var listIds))
                {
                    listIds = new HashSet<Guid>();
                    deadReferences.Add(channelId, listIds);
                }

                listIds.Add(listId);
            }
        }
    }
}
