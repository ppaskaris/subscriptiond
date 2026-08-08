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
        private readonly CosmosRecoveryStore _recoveryStore;
        private readonly string _owner = $"projection:{Environment.ProcessId}:{Guid.NewGuid():N}";
        private readonly CosmosRecoveryInterleavingHooks _interleavingHooks;

        public CosmosListProjectionRepository(
            Container lists,
            Container channels,
            IAppClock clock)
            : this(lists, channels, null, clock, null)
        {
        }

        internal CosmosListProjectionRepository(
            Container lists,
            Container channels,
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions recoveryOptions,
            CosmosRecoveryInterleavingHooks interleavingHooks = null)
        {
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _recoveryStore = recovery == null
                ? null
                : new CosmosRecoveryStore(
                    recovery,
                    clock,
                    recoveryOptions ?? new CosmosRecoveryOptions());
            _interleavingHooks = interleavingHooks;
        }

        public async Task UpdateProjectedChannelsAsync(
            IReadOnlyCollection<Channel> refreshedChannels,
            CancellationToken cancellationToken)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ProjectionFanOut);
            ArgumentNullException.ThrowIfNull(refreshedChannels);
            if (_recoveryStore != null)
            {
                foreach (var channelId in refreshedChannels
                    .Select(channel => channel.Id)
                    .Distinct(StringComparer.Ordinal))
                {
                    await RecoverPendingProjectionAsync(
                        channelId,
                        Constants.ConsistencyRecoveryBatchSize,
                        cancellationToken);
                }

                return;
            }

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

        internal async Task<(int Examined, int Succeeded, bool HasMore)> RecoverPendingProjectionAsync(
            string channelId,
            int take,
            CancellationToken cancellationToken)
        {
            return await RecoverPendingProjectionAsync(
                channelId,
                take,
                () => true,
                cancellationToken);
        }

        internal async Task<(int Examined, int Succeeded, bool HasMore)> RecoverPendingProjectionAsync(
            string channelId,
            int take,
            Func<bool> tryAdmitItem,
            CancellationToken cancellationToken)
        {
            var channel = await ReadChannelAsync(channelId, cancellationToken);
            if (channel == null || !channel.ProjectionRecoveryPending)
            {
                return (0, 0, false);
            }

            var projectionVersion = channel.ProjectionVersion;
            var subscriptionGeneration = channel.SubscriptionGeneration;
            var afterListId =
                channel.ProjectionRecoveryProjectionVersion == projectionVersion
                && channel.ProjectionRecoverySubscriptionGeneration == subscriptionGeneration
                    ? channel.ProjectionRecoveryAfterListId
                    : null;
            var listIds = channel.SubscribedListIds
                .Where(value => Guid.TryParse(value, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Where(value => afterListId == null
                    || string.CompareOrdinal(value, afterListId) > 0)
                .Take(Math.Min(take, Constants.ConsistencyRecoveryBatchSize))
                .ToArray();
            var projected = CosmosDocumentMapper.ToProjectedChannelDocument(
                CosmosDocumentMapper.ToChannel(channel));
            var examined = 0;
            var succeeded = 0;
            foreach (var listIdText in listIds)
            {
                if (!tryAdmitItem())
                {
                    return (examined, succeeded, true);
                }

                examined++;
                if (!Guid.TryParse(listIdText, out var listId))
                {
                    continue;
                }

                var projectionWritten = await UpdateSingleListProjectionAsync(
                    listId,
                    projected,
                    cancellationToken);
                if (projectionWritten)
                {
                    await ReportProjectionSideEffectAsync(channelId, "ListProjected");
                }
                if (projectionWritten
                    && _interleavingHooks?.AfterProjectionListWriteAsync != null)
                {
                    await _interleavingHooks.AfterProjectionListWriteAsync(listIdText);
                }
                if (!projectionWritten)
                {
                    var lifecycle = await _recoveryStore.CreateLifecycleWithResultAsync(
                        listIdText,
                        _clock.UtcNow,
                        cancellationToken);
                    if (lifecycle.Created)
                    {
                        await ReportProjectionSideEffectAsync(
                            channelId,
                            "DeadLifecycleCreated");
                    }
                    var edge = await _recoveryStore.ActivateCandidateAsync(
                        listIdText,
                        channelId,
                        _owner,
                        cancellationToken);
                    await ReportProjectionSideEffectAsync(channelId, "DeadEdgeActivated");
                    await _recoveryStore.MarkDueAsync(edge, cancellationToken);
                    await ReportProjectionSideEffectAsync(channelId, "DeadEdgeDue");
                }

                succeeded++;
                var checkpointed = await CheckpointProjectionAsync(
                    channelId,
                    projectionVersion,
                    subscriptionGeneration,
                    listIdText,
                    clearPending: false,
                    cancellationToken);
                if (checkpointed == ConditionalWriteResult.NotApplicable)
                {
                    return (examined, succeeded, true);
                }
                await ReportProjectionSideEffectAsync(channelId, "CheckpointSaved");
            }

            var latest = await ReadChannelAsync(channelId, cancellationToken);
            if (latest == null)
            {
                return (examined, succeeded, false);
            }

            if (latest.ProjectionVersion != projectionVersion
                || latest.SubscriptionGeneration != subscriptionGeneration)
            {
                var reset = await ResetProjectionCheckpointAsync(
                    latest,
                    cancellationToken);
                if (reset)
                {
                    await ReportProjectionSideEffectAsync(channelId, "CheckpointReset");
                }
                return (examined, succeeded, true);
            }

            var lastProcessedListId = listIds.LastOrDefault() ?? afterListId;
            var remaining = latest.SubscribedListIds
                .Where(value => Guid.TryParse(value, out _))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Any(value => lastProcessedListId == null
                    || string.CompareOrdinal(value, lastProcessedListId) > 0);
            if (remaining)
            {
                return (examined, succeeded, true);
            }

            if (listIds.Length == 0 && !tryAdmitItem())
            {
                return (examined, succeeded, true);
            }

            var cleared = await CheckpointProjectionAsync(
                channelId,
                projectionVersion,
                subscriptionGeneration,
                listIds.LastOrDefault(),
                clearPending: true,
                cancellationToken);
            if (cleared == ConditionalWriteResult.Written)
            {
                await ReportProjectionSideEffectAsync(channelId, "PendingCleared");
            }
            else
            {
                return (examined, succeeded, true);
            }
            return (examined, succeeded, false);
        }

        private Task ReportProjectionSideEffectAsync(string channelId, string sideEffect)
        {
            return _interleavingHooks?.AfterProjectionSideEffectAsync?.Invoke(
                    channelId,
                    sideEffect)
                ?? Task.CompletedTask;
        }

        private async Task<bool> UpdateSingleListProjectionAsync(
            Guid listId,
            CosmosProjectedChannelDocument projected,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var list = await ReadListAsync(listId, cancellationToken);
                if (list == null)
                {
                    return false;
                }

                var found = false;
                list.Channels = list.Channels.Select(channel =>
                {
                    if (!string.Equals(channel.Id, projected.Id, StringComparison.Ordinal))
                    {
                        return channel;
                    }

                    found = true;
                    return projected;
                }).ToArray();
                if (!found)
                {
                    return false;
                }

                list.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    list.ExpiredAfter,
                    _clock.UtcNow);
                var bounded = CosmosListProjectionPolicy.CreateBoundedCopy(list, _clock.UtcNow);
                try
                {
                    await _lists.ReplaceItemAsync(
                        bounded,
                        bounded.Id,
                        new PartitionKey(bounded.Id),
                        new ItemRequestOptions { IfMatchEtag = bounded.ETag },
                        cancellationToken);
                    return true;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 >= MaxWriteAttempts)
                    {
                        throw new CosmosRecoveryConflictException(
                            $"List '{listId:D}' projection write conflicted twice.");
                    }
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ProjectionRepository",
                        "UpdateListProjection",
                        retry: true);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"List '{listId:D}' projection write conflicted twice.");
        }

        private async Task<ConditionalWriteResult> CheckpointProjectionAsync(
            string channelId,
            long projectionVersion,
            long subscriptionGeneration,
            string afterListId,
            bool clearPending,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var channel = await ReadChannelAsync(channelId, cancellationToken);
                if (channel == null
                    || channel.ProjectionVersion != projectionVersion
                    || channel.SubscriptionGeneration != subscriptionGeneration)
                {
                    return ConditionalWriteResult.NotApplicable;
                }

                channel.ProjectionRecoveryProjectionVersion = projectionVersion;
                channel.ProjectionRecoverySubscriptionGeneration = subscriptionGeneration;
                channel.ProjectionRecoveryAfterListId = clearPending ? null : afterListId;
                if (clearPending)
                {
                    channel.ProjectionRecoveryPending = false;
                    channel.ProjectionRecoveryDueAt = null;
                    channel.ProjectionRecoveryStartedAt = null;
                    channel.ProjectionRecoveryAttempt = 0;
                    channel.ProjectionRecoveryPoison = false;
                    channel.ProjectionRecoveryLastErrorClass = null;
                }

                try
                {
                    await _channels.ReplaceItemAsync(
                        channel,
                        channel.Id,
                        new PartitionKey(channel.Id),
                        new ItemRequestOptions { IfMatchEtag = channel.ETag },
                        cancellationToken);
                    return ConditionalWriteResult.Written;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 >= MaxWriteAttempts)
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Channel '{channelId}' projection checkpoint conflicted twice.");
                    }
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ProjectionRepository",
                        "SaveProjectionCheckpoint",
                        retry: true);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Channel '{channelId}' projection checkpoint conflicted twice.");
        }

        private async Task<bool> ResetProjectionCheckpointAsync(
            CosmosChannelDocument channel,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                channel.ProjectionRecoveryProjectionVersion = null;
                channel.ProjectionRecoverySubscriptionGeneration = null;
                channel.ProjectionRecoveryAfterListId = null;
                channel.ProjectionRecoveryDueAt = _clock.UtcNow;
                try
                {
                    await _channels.ReplaceItemAsync(
                        channel,
                        channel.Id,
                        new PartitionKey(channel.Id),
                        new ItemRequestOptions { IfMatchEtag = channel.ETag },
                        cancellationToken);
                    return true;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 >= MaxWriteAttempts)
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Channel '{channel.Id}' projection checkpoint reset conflicted twice.");
                    }
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ProjectionRepository",
                        "ResetProjectionCheckpoint",
                        retry: true);
                    channel = await ReadChannelAsync(channel.Id, cancellationToken);
                    if (channel == null)
                    {
                        return false;
                    }
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Channel '{channel.Id}' projection checkpoint reset conflicted twice.");
        }

        private enum ConditionalWriteResult
        {
            NotApplicable,
            Written
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
                var boundedDocument = CosmosListProjectionPolicy.CreateBoundedCopy(
                    document,
                    _clock.UtcNow);

                try
                {
                    await _lists.ReplaceItemAsync(
                        boundedDocument,
                        boundedDocument.Id,
                        new PartitionKey(boundedDocument.Id),
                        new ItemRequestOptions { IfMatchEtag = boundedDocument.ETag },
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
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 >= MaxWriteAttempts)
                    {
                        throw new CosmosRecoveryConflictException(
                            $"List '{listId:D}' projection write conflicted twice.");
                    }
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ProjectionRepository",
                        "RepairDeadReference",
                        retry: true);
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
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 >= MaxWriteAttempts)
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Channel '{channelId}' projection reference repair conflicted twice.");
                    }
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ProjectionRepository",
                        "UpdateProjection",
                        retry: true);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Channel '{channelId}' projection reference repair conflicted twice.");
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
