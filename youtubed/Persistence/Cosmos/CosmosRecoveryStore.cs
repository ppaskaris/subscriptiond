using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosRecoveryStore
    {
        private const int MaxWriteAttempts = 2;

        private readonly Container _recovery;
        private readonly IAppClock _clock;
        private readonly CosmosRecoveryOptions _options;

        internal CosmosRecoveryStore(
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions options)
        {
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
        }

        internal static string GetEdgeId(string channelId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(channelId));
            return "edge:" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal async Task<CosmosRecoveryLifecycleDocument> CreateLifecycleAsync(
            string listId,
            DateTimeOffset expiredAfter,
            CancellationToken cancellationToken)
        {
            var existing = await ReadLifecycleAsync(listId, cancellationToken);
            if (existing != null)
            {
                return existing;
            }

            var document = new CosmosRecoveryLifecycleDocument
            {
                ListId = listId,
                ExpiredAfter = expiredAfter,
                NextCheckAt = expiredAfter,
                NextAttemptAt = _clock.UtcNow
            };
            EnsureBounded(document);
            try
            {
                var response = await _recovery.CreateItemAsync(
                    document,
                    new PartitionKey(listId),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                CosmosRecoveryTelemetry.RecordConflict(
                    "RecoveryStore",
                    "CreateLifecycle",
                    retry: false);
                return await ReadLifecycleAsync(listId, cancellationToken);
            }
        }

        internal async Task RenewLifecycleAsync(
            string listId,
            DateTimeOffset expiredAfter,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var lifecycle = await CreateLifecycleAsync(
                    listId,
                    expiredAfter,
                    cancellationToken);
                lifecycle.ExpiredAfter = expiredAfter;
                lifecycle.NextCheckAt = expiredAfter;
                lifecycle.NextAttemptAt = expiredAfter;
                lifecycle.State = "Active";
                lifecycle.CleanupEdgeAfterChannelId = null;
                lifecycle.CleanupEdgeAfterId = null;
                lifecycle.CleanupTraversalEdgeGeneration = null;
                lifecycle.MissingObservedAt = null;
                lifecycle.Owner = null;
                lifecycle.LeaseUntil = null;
                lifecycle.Attempt = 0;
                lifecycle.LastErrorClass = null;
                try
                {
                    await ReplaceLifecycleAsync(lifecycle, cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "RenewLifecycle",
                        retry: true);
                }
            }
        }

        internal async Task<CosmosRecoveryLifecycleDocument> MarkDeletingAsync(
            string listId,
            DateTimeOffset? expiredAfter,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var lifecycle = await ReadLifecycleAsync(listId, cancellationToken);
                if (lifecycle == null)
                {
                    if (!expiredAfter.HasValue)
                    {
                        return null;
                    }

                    lifecycle = await CreateLifecycleAsync(
                        listId,
                        expiredAfter.Value,
                        cancellationToken);
                }

                if (expiredAfter.HasValue)
                {
                    lifecycle.ExpiredAfter = expiredAfter.Value;
                }
                lifecycle.State = "Deleting";
                lifecycle.NextCheckAt = _clock.UtcNow;
                lifecycle.NextAttemptAt = _clock.UtcNow;
                lifecycle.MissingObservedAt = null;
                lifecycle.CleanupEdgeAfterChannelId = null;
                lifecycle.CleanupEdgeAfterId = null;
                lifecycle.CleanupTraversalEdgeGeneration = null;
                lifecycle.Owner = null;
                lifecycle.LeaseUntil = null;
                lifecycle.Attempt = 0;
                lifecycle.LastErrorClass = null;
                try
                {
                    return (await ReplaceLifecycleAsync(lifecycle, cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "MarkDeleting",
                        retry: true);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{listId}' deletion state conflicted twice.");
        }

        internal async Task<CosmosRecoveryLifecycleDocument> ReschedulePresentLifecycleAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            DateTimeOffset expiredAfter,
            CancellationToken cancellationToken)
        {
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.State = "Active";
                lifecycle.ExpiredAfter = expiredAfter;
                lifecycle.NextCheckAt = expiredAfter > _clock.UtcNow
                    ? expiredAfter
                    : _clock.UtcNow.Add(Constants.ConsistencyRecoveryLifecycleRecheckInterval);
                lifecycle.NextAttemptAt = lifecycle.NextCheckAt;
                lifecycle.CleanupEdgeAfterChannelId = null;
                lifecycle.CleanupEdgeAfterId = null;
                lifecycle.CleanupTraversalEdgeGeneration = null;
                lifecycle.MissingObservedAt = null;
                lifecycle.Owner = null;
                lifecycle.LeaseUntil = null;
                lifecycle.Attempt = 0;
                lifecycle.LastErrorClass = null;
                try
                {
                    return (await ReplaceLifecycleAsync(lifecycle, cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "RescheduleLifecycle",
                        retry: true);
                    var latest = await ReadLifecycleAsync(lifecycle.ListId, cancellationToken);
                    if (latest == null
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed before rescheduling.");
                    }

                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' rescheduling conflicted twice.");
        }

        internal async Task<CosmosRecoveryEdgeDocument> ActivateCandidateAsync(
            string listId,
            string channelId,
            string owner,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(owner);
            var edgeId = GetEdgeId(channelId);
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var lifecycle = await ReadLifecycleAsync(listId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Recovery lifecycle '{listId}' must exist before activating an edge.");
                var edge = await ReadEdgeAsync(listId, edgeId, cancellationToken);
                if (edge != null)
                {
                    if (edge.LeaseUntil > _clock.UtcNow
                        && !string.Equals(edge.Owner, owner, StringComparison.Ordinal))
                    {
                        throw new RecoveryLeaseUnavailableException(
                            $"Recovery edge '{edge.Id}' is owned by another active mutation.");
                    }

                    if (edge.LeaseUntil > _clock.UtcNow
                        && string.Equals(edge.Owner, owner, StringComparison.Ordinal)
                        && string.Equals(edge.State, "Candidate", StringComparison.Ordinal))
                    {
                        return edge;
                    }

                    edge.State = "Candidate";
                    edge.Generation++;
                    edge.Owner = owner;
                    edge.LeaseUntil = _clock.UtcNow.Add(_options.LeaseDuration);
                    edge.NextAttemptAt = edge.LeaseUntil;
                    edge.LastErrorClass = null;
                    EnsureBounded(edge);
                    try
                    {
                        var replaceResponse = await _recovery.ReplaceItemAsync(
                            edge,
                            edge.Id,
                            new PartitionKey(listId),
                            new ItemRequestOptions { IfMatchEtag = edge.ETag },
                            cancellationToken);
                        return replaceResponse.Resource;
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.PreconditionFailed
                        && attempt + 1 < MaxWriteAttempts)
                    {
                        CosmosRecoveryTelemetry.RecordConflict(
                            "RecoveryStore",
                            "ActivateCandidate",
                            retry: true);
                        continue;
                    }
                }

                if (lifecycle.ActiveEdgeCount >= _options.MaxActiveEdgesPerList)
                {
                    throw new RecoveryCapacityExceededException(
                        $"List '{listId}' has reached the {_options.MaxActiveEdgesPerList}-edge recovery capacity.");
                }

                lifecycle.ActiveEdgeCount++;
                lifecycle.EdgeGeneration++;
                var created = new CosmosRecoveryEdgeDocument
                {
                    Id = edgeId,
                    ListId = listId,
                    ChannelId = channelId,
                    Generation = 1,
                    Owner = owner,
                    LeaseUntil = _clock.UtcNow.Add(_options.LeaseDuration),
                    NextAttemptAt = _clock.UtcNow.Add(_options.LeaseDuration)
                };
                EnsureBounded(lifecycle);
                EnsureBounded(created);

                var batch = _recovery.CreateTransactionalBatch(new PartitionKey(listId))
                    .ReplaceItem(
                        lifecycle.Id,
                        lifecycle,
                        new TransactionalBatchItemRequestOptions
                        {
                            IfMatchEtag = lifecycle.ETag
                        })
                    .CreateItem(created);
                using var response = await batch.ExecuteAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response.GetOperationResultAtIndex<CosmosRecoveryEdgeDocument>(1).Resource;
                }

                if ((response.StatusCode == HttpStatusCode.PreconditionFailed
                        || response.StatusCode == HttpStatusCode.Conflict)
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "ActivateCandidateBatch",
                        retry: true);
                    continue;
                }

                throw CreateBatchException(response, "activate recovery edge");
            }

            throw new InvalidOperationException("Recovery edge activation did not converge.");
        }

        internal async Task<CosmosRecoveryEdgeDocument> MarkTrackedAsync(
            CosmosRecoveryEdgeDocument edge,
            long membershipVersion,
            CancellationToken cancellationToken)
        {
            var expectedGeneration = edge.Generation;
            var expectedOwner = edge.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                edge.State = "Tracked";
                edge.Owner = null;
                edge.LeaseUntil = null;
                edge.NextAttemptAt = null;
                edge.Attempt = 0;
                edge.LastObservedMembershipVersion = membershipVersion;
                edge.LastErrorClass = null;
                EnsureBounded(edge);
                try
                {
                    var response = await _recovery.ReplaceItemAsync(
                        edge,
                        edge.Id,
                        new PartitionKey(edge.ListId),
                        new ItemRequestOptions { IfMatchEtag = edge.ETag },
                        cancellationToken);
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "MarkTracked",
                        retry: true);
                    var latest = await ReadEdgeAsync(edge.ListId, edge.Id, cancellationToken);
                    if (latest == null
                        || latest.Generation != expectedGeneration
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery edge '{edge.Id}' changed generation or owner before tracking.");
                    }

                    edge = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery edge '{edge.Id}' tracking conflicted twice.");
        }

        internal async Task<CosmosRecoveryEdgeDocument> MarkDueAsync(
            CosmosRecoveryEdgeDocument edge,
            CancellationToken cancellationToken)
        {
            var expectedGeneration = edge.Generation;
            var expectedOwner = edge.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                edge.State = "Due";
                edge.Owner = null;
                edge.LeaseUntil = null;
                edge.NextAttemptAt = _clock.UtcNow;
                try
                {
                    var response = await _recovery.ReplaceItemAsync(
                        edge,
                        edge.Id,
                        new PartitionKey(edge.ListId),
                        new ItemRequestOptions { IfMatchEtag = edge.ETag },
                        cancellationToken);
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "MarkDue",
                        retry: true);
                    var latest = await ReadEdgeAsync(edge.ListId, edge.Id, cancellationToken);
                    if (latest == null
                        || latest.Generation != expectedGeneration
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery edge '{edge.Id}' changed generation or owner before due.");
                    }

                    edge = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery edge '{edge.Id}' due write conflicted twice.");
        }

        internal async Task<CosmosRecoveryLifecycleDocument> ClaimLifecycleAsync(
            string listId,
            string owner,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var lifecycle = await ReadLifecycleAsync(listId, cancellationToken);
                if (lifecycle == null)
                {
                    return null;
                }

                var now = _clock.UtcNow;
                if (lifecycle.LeaseUntil > now
                    && !string.Equals(lifecycle.Owner, owner, StringComparison.Ordinal))
                {
                    return null;
                }

                var leaseTakenOver = !string.IsNullOrWhiteSpace(lifecycle.Owner)
                    && !string.Equals(lifecycle.Owner, owner, StringComparison.Ordinal);
                lifecycle.Owner = owner;
                lifecycle.LeaseUntil = now.Add(_options.LeaseDuration);
                try
                {
                    var response = await ReplaceLifecycleAsync(lifecycle, cancellationToken);
                    response.Resource.LeaseTakenOver = leaseTakenOver;
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "ClaimLifecycle",
                        retry: true);
                }
            }

            return null;
        }

        internal async Task<CosmosRecoveryEdgeDocument> ClaimEdgeAsync(
            string listId,
            string edgeId,
            string owner,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var edge = await ReadEdgeAsync(listId, edgeId, cancellationToken);
                if (edge == null || !edge.Active)
                {
                    return null;
                }

                var now = _clock.UtcNow;
                if (edge.LeaseUntil > now
                    && !string.Equals(edge.Owner, owner, StringComparison.Ordinal))
                {
                    return null;
                }

                var leaseTakenOver = !string.IsNullOrWhiteSpace(edge.Owner)
                    && !string.Equals(edge.Owner, owner, StringComparison.Ordinal);
                edge.Owner = owner;
                edge.LeaseUntil = now.Add(_options.LeaseDuration);
                try
                {
                    var response = await _recovery.ReplaceItemAsync(
                        edge,
                        edge.Id,
                        new PartitionKey(listId),
                        new ItemRequestOptions { IfMatchEtag = edge.ETag },
                        cancellationToken);
                    response.Resource.LeaseTakenOver = leaseTakenOver;
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "ClaimEdge",
                        retry: true);
                }
            }

            return null;
        }

        internal async Task<CosmosRecoveryLifecycleDocument> MarkMissingAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            CancellationToken cancellationToken)
        {
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.State = "Cleaning";
                var firstMissingObservation = !lifecycle.MissingObservedAt.HasValue;
                lifecycle.MissingObservedAt ??= _clock.UtcNow;
                lifecycle.NextCheckAt = _clock.UtcNow;
                lifecycle.NextAttemptAt = _clock.UtcNow;
                if (firstMissingObservation)
                {
                    lifecycle.Attempt = 0;
                    lifecycle.LastErrorClass = null;
                }
                try
                {
                    return (await ReplaceLifecycleAsync(lifecycle, cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "MarkMissing",
                        retry: true);
                    var latest = await ReadLifecycleAsync(lifecycle.ListId, cancellationToken);
                    if (latest == null
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed before its 404 observation was recorded.");
                    }

                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' 404 observation conflicted twice.");
        }

        internal async Task<CosmosRecoveryLifecycleDocument> SaveMembershipCheckpointAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            long membershipVersion,
            long traversalGeneration,
            string afterChannelId,
            string afterId,
            bool releaseLease,
            CancellationToken cancellationToken)
        {
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.MembershipVersionBeingRepaired = membershipVersion;
                lifecycle.MembershipTraversalEdgeGeneration = traversalGeneration;
                lifecycle.MembershipEdgeAfterChannelId = afterChannelId;
                lifecycle.MembershipEdgeAfterId = afterId;
                if (releaseLease)
                {
                    lifecycle.Owner = null;
                    lifecycle.LeaseUntil = null;
                }

                EnsureBounded(lifecycle);
                try
                {
                    var response = await ReplaceLifecycleAsync(lifecycle, cancellationToken);
                    return response.Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "MembershipCheckpoint",
                        retry: true);
                    var latest = await ReadLifecycleAsync(
                        lifecycle.ListId,
                        cancellationToken);
                    if (latest == null
                        || latest.EdgeGeneration != traversalGeneration
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed before checkpoint.");
                    }

                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' checkpoint conflicted twice.");
        }

        internal async Task<CosmosRecoveryLifecycleDocument> SaveCleanupCheckpointAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            long traversalGeneration,
            string afterChannelId,
            string afterId,
            bool releaseLease,
            CancellationToken cancellationToken)
        {
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.CleanupTraversalEdgeGeneration = traversalGeneration;
                lifecycle.CleanupEdgeAfterChannelId = afterChannelId;
                lifecycle.CleanupEdgeAfterId = afterId;
                lifecycle.NextCheckAt = _clock.UtcNow;
                lifecycle.NextAttemptAt = _clock.UtcNow;
                if (releaseLease)
                {
                    lifecycle.Owner = null;
                    lifecycle.LeaseUntil = null;
                }

                EnsureBounded(lifecycle);
                try
                {
                    return (await ReplaceLifecycleAsync(lifecycle, cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "CleanupCheckpoint",
                        retry: true);
                    var latest = await ReadLifecycleAsync(lifecycle.ListId, cancellationToken);
                    if (latest == null
                        || latest.EdgeGeneration != traversalGeneration
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed before cleanup checkpoint.");
                    }

                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' cleanup checkpoint conflicted twice.");
        }

        internal async Task<(bool Retired, long EdgeGeneration)> RetireEdgeAsync(
            CosmosRecoveryEdgeDocument edge,
            CosmosRecoveryLifecycleDocument lifecycle,
            long membershipVersion,
            string afterChannelId,
            string afterId,
            CancellationToken cancellationToken,
            bool adoptMembershipCheckpoint = false,
            bool adoptCleanupCheckpoint = false,
            Func<CancellationToken, Task<bool>> revalidateAuthoritativeAbsenceAsync = null)
        {
            var expectedEdgeGeneration = edge.Generation;
            var expectedEdgeOwner = edge.Owner;
            var expectedLifecycleGeneration = lifecycle.EdgeGeneration;
            var expectedLifecycleOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                if (lifecycle.ActiveEdgeCount <= 0)
                {
                    return (false, lifecycle.EdgeGeneration);
                }

                var priorGeneration = lifecycle.EdgeGeneration;
                lifecycle.ActiveEdgeCount--;
                lifecycle.EdgeGeneration++;
                if (adoptMembershipCheckpoint)
                {
                    lifecycle.MembershipVersionBeingRepaired = membershipVersion;
                    lifecycle.MembershipTraversalEdgeGeneration = lifecycle.EdgeGeneration;
                    lifecycle.MembershipEdgeAfterChannelId = afterChannelId;
                    lifecycle.MembershipEdgeAfterId = afterId;
                }
                if (adoptCleanupCheckpoint)
                {
                    lifecycle.CleanupTraversalEdgeGeneration = lifecycle.EdgeGeneration;
                    lifecycle.CleanupEdgeAfterChannelId = afterChannelId;
                    lifecycle.CleanupEdgeAfterId = afterId;
                    lifecycle.NextCheckAt = _clock.UtcNow;
                    lifecycle.NextAttemptAt = _clock.UtcNow;
                }
                EnsureBounded(lifecycle);

                var batch = _recovery.CreateTransactionalBatch(new PartitionKey(edge.ListId))
                    .ReplaceItem(
                        lifecycle.Id,
                        lifecycle,
                        new TransactionalBatchItemRequestOptions
                        {
                            IfMatchEtag = lifecycle.ETag
                        })
                    .DeleteItem(
                        edge.Id,
                        new TransactionalBatchItemRequestOptions
                        {
                            IfMatchEtag = edge.ETag
                        });
                using var response = await batch.ExecuteAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var saved = response
                        .GetOperationResultAtIndex<CosmosRecoveryLifecycleDocument>(0)
                        .Resource;
                    lifecycle.ETag = saved.ETag;
                    return (true, saved.EdgeGeneration);
                }

                if (!IsSemanticRetirementContention(response))
                {
                    throw CreateBatchException(response, "retire recovery edge");
                }

                if (attempt + 1 >= MaxWriteAttempts
                    || revalidateAuthoritativeAbsenceAsync == null
                    || !await revalidateAuthoritativeAbsenceAsync(cancellationToken))
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "RetireEdge",
                        retry: false);
                    return (false, priorGeneration);
                }

                var latestLifecycle = await ReadLifecycleAsync(
                    lifecycle.ListId,
                    cancellationToken);
                var latestEdge = await ReadEdgeAsync(
                    edge.ListId,
                    edge.Id,
                    cancellationToken);
                if (latestLifecycle == null
                    || latestEdge == null
                    || !latestEdge.Active
                    || latestEdge.Generation != expectedEdgeGeneration
                    || !string.Equals(
                        latestEdge.Owner,
                        expectedEdgeOwner,
                        StringComparison.Ordinal)
                    || latestLifecycle.EdgeGeneration != expectedLifecycleGeneration
                    || !string.Equals(
                        latestLifecycle.Owner,
                        expectedLifecycleOwner,
                        StringComparison.Ordinal))
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "RetireEdge",
                        retry: false);
                    return (false, priorGeneration);
                }

                CosmosRecoveryTelemetry.RecordConflict(
                    "RecoveryStore",
                    "RetireEdge",
                    retry: true);
                lifecycle = latestLifecycle;
                edge = latestEdge;
            }

            return (false, lifecycle.EdgeGeneration - 1);
        }

        internal async Task FailEdgeAsync(
            CosmosRecoveryEdgeDocument edge,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var expectedGeneration = edge.Generation;
            var expectedOwner = edge.Owner;
            for (var writeAttempt = 0; writeAttempt < MaxWriteAttempts; writeAttempt++)
            {
                edge.Attempt++;
                edge.Owner = null;
                edge.LeaseUntil = null;
                edge.LastErrorClass = exception.GetType().Name;
                if (edge.Attempt >= _options.PoisonAttemptCount)
                {
                    edge.State = "Poison";
                    edge.NextAttemptAt =
                        _clock.UtcNow.Add(Constants.ConsistencyRecoveryPoisonBackoff);
                }
                else
                {
                    var exponent = Math.Min(edge.Attempt - 1, 6);
                    var max = TimeSpan.FromTicks(Math.Min(
                        Constants.ConsistencyRecoveryMaxBackoff.Ticks,
                        Constants.ConsistencyRecoveryMinBackoff.Ticks * (1L << exponent)));
                    edge.NextAttemptAt = _clock.UtcNow.Add(
                        max <= Constants.ConsistencyRecoveryMinBackoff
                            ? Constants.ConsistencyRecoveryMinBackoff
                            : _clock.RandomDelay(Constants.ConsistencyRecoveryMinBackoff, max));
                }

                EnsureBounded(edge);
                try
                {
                    await _recovery.ReplaceItemAsync(
                        edge,
                        edge.Id,
                        new PartitionKey(edge.ListId),
                        new ItemRequestOptions { IfMatchEtag = edge.ETag },
                        cancellationToken);
                    return;
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.PreconditionFailed
                    && writeAttempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "FailEdge",
                        retry: true);
                    var latest = await ReadEdgeAsync(edge.ListId, edge.Id, cancellationToken);
                    if (latest == null)
                    {
                        return;
                    }

                    if (latest.Generation != expectedGeneration
                        || !string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery edge '{edge.Id}' changed generation or owner before failure persistence.");
                    }

                    edge = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery edge '{edge.Id}' failure persistence conflicted twice.");
        }

        internal async Task<IReadOnlyList<CosmosRecoveryEdgeDocument>> QueryMembershipEdgesAsync(
            string listId,
            string afterChannelId,
            string afterId,
            int take,
            CancellationToken cancellationToken)
        {
            var query = new QueryDefinition(
                    "SELECT TOP @take * FROM c WHERE c.kind = \"Edge\" AND c.active = true " +
                    "AND (c.channelId > @afterChannelId OR " +
                    "(c.channelId = @afterChannelId AND c.id > @afterId)) " +
                    "ORDER BY c.channelId ASC, c.id ASC")
                .WithParameter("@take", Math.Min(take, Constants.ConsistencyRecoveryBatchSize))
                .WithParameter("@afterChannelId", afterChannelId ?? string.Empty)
                .WithParameter("@afterId", afterId ?? string.Empty);
            var results = new List<CosmosRecoveryEdgeDocument>();
            using var iterator = _recovery.GetItemQueryIterator<CosmosRecoveryEdgeDocument>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(listId),
                    MaxItemCount = Math.Min(take, Constants.ConsistencyRecoveryBatchSize)
                });
            if (iterator.HasMoreResults)
            {
                results.AddRange(await iterator.ReadNextAsync(cancellationToken));
            }

            return results;
        }

        internal async Task<CosmosRecoveryEdgeDocument> QueryFirstActiveEdgeAsync(
            string listId,
            CancellationToken cancellationToken)
        {
            var edges = await QueryMembershipEdgesAsync(
                listId,
                null,
                null,
                1,
                cancellationToken);
            return edges.FirstOrDefault();
        }

        internal async Task<int> CountActiveEdgesAsync(
            string listId,
            CancellationToken cancellationToken)
        {
            var query = new QueryDefinition(
                    "SELECT VALUE COUNT(1) FROM c WHERE c.kind = \"Edge\" AND c.active = true")
                ;
            using var iterator = _recovery.GetItemQueryIterator<int>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(listId),
                    MaxItemCount = 1
                });
            return iterator.HasMoreResults
                ? (await iterator.ReadNextAsync(cancellationToken)).Single()
                : 0;
        }

        internal async Task<CosmosRecoveryLifecycleDocument> CorrectActiveEdgeCountAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            int activeEdgeCount,
            CancellationToken cancellationToken)
        {
            if (activeEdgeCount < 0 || activeEdgeCount > _options.MaxActiveEdgesPerList)
            {
                throw new InvalidOperationException(
                    $"Recovery lifecycle '{lifecycle.ListId}' recount exceeded the supported edge bound.");
            }

            var expectedGeneration = lifecycle.EdgeGeneration;
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.ActiveEdgeCount = activeEdgeCount;
                lifecycle.EdgeGeneration = expectedGeneration + 1;
                lifecycle.CleanupTraversalEdgeGeneration = null;
                lifecycle.CleanupEdgeAfterChannelId = null;
                lifecycle.CleanupEdgeAfterId = null;
                lifecycle.Attempt = Math.Max(
                    lifecycle.Attempt,
                    _options.PoisonAttemptCount);
                lifecycle.LastErrorClass = "ActiveEdgeCountDrift";
                lifecycle.NextCheckAt = _clock.UtcNow;
                lifecycle.NextAttemptAt = _clock.UtcNow;
                lifecycle.Owner = null;
                lifecycle.LeaseUntil = null;
                try
                {
                    return (await ReplaceLifecycleAsync(
                        lifecycle,
                        cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "CorrectActiveEdgeCount",
                        retry: true);
                    var latest = await ReadLifecycleAsync(
                        lifecycle.ListId,
                        cancellationToken);
                    if (latest == null
                        || latest.EdgeGeneration != expectedGeneration
                        || !string.Equals(
                            latest.Owner,
                            expectedOwner,
                            StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed during bounded recount.");
                    }

                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' recount conflicted twice.");
        }

        internal async Task<bool> DeleteLifecycleAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            CancellationToken cancellationToken)
        {
            try
            {
                await _recovery.DeleteItemAsync<CosmosRecoveryLifecycleDocument>(
                    lifecycle.Id,
                    new PartitionKey(lifecycle.ListId),
                    new ItemRequestOptions { IfMatchEtag = lifecycle.ETag },
                    cancellationToken);
                return true;
            }
            catch (CosmosException exception) when (
                exception.StatusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.PreconditionFailed)
            {
                if (exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "CompleteLifecycle",
                        retry: false);
                }
                return exception.StatusCode == HttpStatusCode.NotFound;
            }
        }

        internal async Task FailLifecycleAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var expectedOwner = lifecycle.Owner;
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                lifecycle.Attempt++;
                lifecycle.Owner = null;
                lifecycle.LeaseUntil = null;
                lifecycle.LastErrorClass = exception.GetType().Name;
                lifecycle.NextCheckAt = lifecycle.Attempt >= _options.PoisonAttemptCount
                    ? _clock.UtcNow.Add(Constants.ConsistencyRecoveryPoisonBackoff)
                    : _clock.UtcNow.Add(GetBackoff(lifecycle.Attempt));
                lifecycle.NextAttemptAt = lifecycle.NextCheckAt;
                try
                {
                    await ReplaceLifecycleAsync(lifecycle, cancellationToken);
                    return;
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "RecoveryStore",
                        "FailLifecycle",
                        retry: true);
                    var latest = await ReadLifecycleAsync(lifecycle.ListId, cancellationToken);
                    if (latest == null)
                    {
                        return;
                    }
                    if (!string.Equals(latest.Owner, expectedOwner, StringComparison.Ordinal))
                    {
                        throw new CosmosRecoveryConflictException(
                            $"Recovery lifecycle '{lifecycle.ListId}' changed owner before failure persistence.");
                    }
                    lifecycle = latest;
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Recovery lifecycle '{lifecycle.ListId}' failure persistence conflicted twice.");
        }

        internal async Task<CosmosRecoveryLifecycleDocument> ReadLifecycleAsync(
            string listId,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _recovery.ReadItemAsync<CosmosRecoveryLifecycleDocument>(
                    CosmosRecoveryLifecycleDocument.DocumentId,
                    new PartitionKey(listId),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        internal async Task<CosmosRecoveryEdgeDocument> ReadEdgeAsync(
            string listId,
            string edgeId,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _recovery.ReadItemAsync<CosmosRecoveryEdgeDocument>(
                    edgeId,
                    new PartitionKey(listId),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private Task<ItemResponse<CosmosRecoveryLifecycleDocument>> ReplaceLifecycleAsync(
            CosmosRecoveryLifecycleDocument lifecycle,
            CancellationToken cancellationToken)
        {
            EnsureBounded(lifecycle);
            return _recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(lifecycle.ListId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag },
                cancellationToken);
        }

        private void EnsureBounded<T>(T document)
        {
            using var stream = CosmosSystemTextJsonSerializer.Instance.ToStream(document);
            if (stream.Length >= _options.RecoveryDocumentSizeCeilingBytes)
            {
                throw new InvalidOperationException(
                    $"Recovery document exceeds the {_options.RecoveryDocumentSizeCeilingBytes}-byte ceiling.");
            }
        }

        private TimeSpan GetBackoff(int attempt)
        {
            var exponent = Math.Min(Math.Max(0, attempt - 1), 6);
            var max = TimeSpan.FromTicks(Math.Min(
                Constants.ConsistencyRecoveryMaxBackoff.Ticks,
                Constants.ConsistencyRecoveryMinBackoff.Ticks * (1L << exponent)));
            return max <= Constants.ConsistencyRecoveryMinBackoff
                ? Constants.ConsistencyRecoveryMinBackoff
                : _clock.RandomDelay(Constants.ConsistencyRecoveryMinBackoff, max);
        }

        private static CosmosException CreateBatchException(
            TransactionalBatchResponse response,
            string operation)
        {
            return new CosmosException(
                $"Failed to {operation}.",
                response.StatusCode,
                0,
                response.ActivityId,
                response.RequestCharge);
        }

        private static bool IsSemanticRetirementContention(
            TransactionalBatchResponse response)
        {
            return response.StatusCode is HttpStatusCode.PreconditionFailed
                or HttpStatusCode.Conflict
                or HttpStatusCode.NotFound;
        }
    }
}
