using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.WebUtilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.SecurityTheatre;
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
        private readonly CosmosRecoveryStore _recoveryStore;
        private readonly IWorkerStateStore _workerStateStore;
        private readonly IWorkerWakeSignal _wakeSignal;
        private readonly CosmosRecoveryOptions _recoveryOptions;
        private readonly CosmosRecoveryInterleavingHooks _interleavingHooks;

        public CosmosListRepository(Container lists, Container channels, IAppClock clock)
            : this(lists, channels, null, clock, null, null, null)
        {
        }

        internal CosmosListRepository(
            Container lists,
            Container channels,
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions recoveryOptions,
            IWorkerStateStore workerStateStore,
            IWorkerWakeSignal wakeSignal,
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
            _recoveryOptions = recoveryOptions ?? new CosmosRecoveryOptions();
            _channelRepository = new CosmosChannelRepository(
                _channels,
                _lists,
                recovery,
                _clock,
                recoveryOptions);
            _workerStateStore = workerStateStore;
            _wakeSignal = wakeSignal;
            _interleavingHooks = interleavingHooks;
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

            if (_recoveryStore != null)
            {
                await _recoveryStore.CreateLifecycleAsync(
                    document.Id,
                    document.ExpiredAfter,
                    CancellationToken.None);
            }

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

        public async Task<ListVideoProjection> GetAuthenticatedVideoProjectionAsync(
            Guid id,
            string token,
            DateTimeOffset expiredAfter,
            DateOnly renewedOn,
            int videoLimit)
        {
            using var requestScope = CosmosRequestChargeScope.Begin();
            var outcome = "error";
            try
            {
                for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
                {
                    var document = await ReadListAsync(id);
                    if (document == null)
                    {
                        outcome = "missing";
                        return null;
                    }

                    if (token == null
                        || TokenUtils.NotEqual(
                            token,
                            WebEncoders.Base64UrlEncode(
                                document.Token ?? Array.Empty<byte>())))
                    {
                        outcome = "rejected_token";
                        return null;
                    }

                    var projection = CosmosDocumentMapper.ToVideoProjection(
                        document,
                        videoLimit);
                    if (document.ExpirationRenewedOn == renewedOn)
                    {
                        outcome = "same_day";
                        return projection;
                    }

                    document.ExpiredAfter = expiredAfter;
                    document.ExpirationRenewedOn = renewedOn;
                    document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                        expiredAfter,
                        _clock.UtcNow);

                    try
                    {
                        await ReplaceAsync(document);
                        outcome = "renewed";
                        return projection;
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.PreconditionFailed
                        && attempt + 1 < MaxWriteAttempts)
                    {
                        outcome = "conflict_retry";
                        CosmosRecoveryTelemetry.RecordConflict(
                            "ListRepository",
                            "AuthenticatedListPageRenewal",
                            retry: true);
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        outcome = "conflict_exhausted";
                        throw;
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.NotFound)
                    {
                        outcome = "concurrent_deletion";
                        return null;
                    }
                }

                throw new InvalidOperationException(
                    "Authenticated list-page renewal conflicted twice.");
            }
            finally
            {
                CosmosListPageTelemetry.Record(
                    requestScope.RequestCount,
                    requestScope.RequestCharge,
                    outcome);
            }
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
                    if (_recoveryStore != null)
                    {
                        await _recoveryStore.RenewLifecycleAsync(
                            document.Id,
                            expiredAfter,
                            CancellationToken.None);
                    }
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "UpdateList",
                        retry: true);
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

            if (_recoveryStore == null)
            {
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
                return;
            }

            var listIdText = listId.ToString("D");
            var existingList = await ReadListAsync(listId);
            if (existingList == null)
            {
                return;
            }

            await EnsureLifecycleForExistingListAsync(existingList);
            var owner = CreateMutationOwner();
            var edge = await _recoveryStore.ActivateCandidateAsync(
                listIdText,
                channelId,
                owner,
                CancellationToken.None);
            if (!await _channelRepository.ReserveSubscriptionAsync(
                channelId,
                listId,
                CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"Canonical channel '{channelId}' was not available for membership reservation.");
            }

            if (_interleavingHooks?.AfterMutationReservationAsync != null)
            {
                await _interleavingHooks.AfterMutationReservationAsync(edge);
            }

            var committedVersion = await UpdateRecoverableMembershipAsync(
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
                },
                edge,
                owner);
            await _channelRepository.RepairSubscriptionFromListTruthAsync(
                channelId,
                listId,
                CancellationToken.None);
            var currentList = await ReadListAsync(listId);
            if (currentList?.Channels.Any(channel => string.Equals(
                channel.Id,
                channelId,
                StringComparison.Ordinal)) == true)
            {
                edge = await _recoveryStore.MarkTrackedAsync(
                    edge,
                    committedVersion,
                    CancellationToken.None);
            }
            else
            {
                var lifecycle = await _recoveryStore.ReadLifecycleAsync(
                    listIdText,
                    CancellationToken.None);
                edge = await _recoveryStore.ReadEdgeAsync(
                    listIdText,
                    edge.Id,
                    CancellationToken.None);
                if (edge != null && lifecycle != null)
                {
                    await _recoveryStore.RetireEdgeAsync(
                        edge,
                        lifecycle,
                        committedVersion,
                        channelId,
                        edge.Id,
                        CancellationToken.None,
                        revalidateAuthoritativeAbsenceAsync: cancellationToken =>
                            IsMembershipAbsentAtVersionAsync(
                                listId,
                                channelId,
                                committedVersion,
                                cancellationToken));
                }
            }
            await ClearMembershipPendingAsync(listId, committedVersion);
            await ForceRecoveryAsync();
        }

        public async Task RemoveChannelAsync(Guid listId, string channelId)
        {
            if (_recoveryStore != null)
            {
                await RemoveChannelRecoverablyAsync(listId, channelId);
                return;
            }

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
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "RemoveChannel",
                        retry: true);
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
            if (_recoveryStore == null)
            {
                await DeleteWithoutRecoveryAsync(id);
                return;
            }

            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(id);
                if (document == null)
                {
                    await _recoveryStore.MarkDeletingAsync(
                        documentId,
                        null,
                        CancellationToken.None);
                    await ForceRecoveryAsync();
                    return;
                }

                await _recoveryStore.MarkDeletingAsync(
                    documentId,
                    document.ExpiredAfter,
                    CancellationToken.None);
                if (_interleavingHooks?.AfterLifecycleSideEffectAsync != null)
                {
                    await _interleavingHooks.AfterLifecycleSideEffectAsync(
                        documentId,
                        "Deleting");
                }

                await EnsureLifecycleForExistingListAsync(
                    document,
                    reportLifecycleSideEffects: true);
                try
                {
                    await _lists.DeleteItemAsync<CosmosListDocument>(
                        documentId,
                        new PartitionKey(documentId),
                        new ItemRequestOptions { IfMatchEtag = document.ETag });
                    if (_interleavingHooks?.AfterLifecycleSideEffectAsync != null)
                    {
                        await _interleavingHooks.AfterLifecycleSideEffectAsync(
                            documentId,
                            "ListDeleted");
                    }
                    foreach (var channelId in document.Channels
                        .Where(channel => channel != null)
                        .Select(channel => channel.Id)
                        .Distinct(StringComparer.Ordinal))
                    {
                        await _channelRepository.RepairSubscriptionFromListTruthAsync(
                            channelId,
                            id,
                            CancellationToken.None);
                        if (_interleavingHooks?.AfterLifecycleSideEffectAsync != null)
                        {
                            await _interleavingHooks.AfterLifecycleSideEffectAsync(
                                documentId,
                                $"ChannelRepaired:{channelId}");
                        }
                    }
                    await ForceRecoveryAsync();
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "ReplaceList",
                        retry: true);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    await ForceRecoveryAsync();
                    return;
                }
            }
        }

        private async Task DeleteWithoutRecoveryAsync(Guid id)
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
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "DeleteList",
                        retry: true);
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
            }

            foreach (var channelId in deletedDocument?.Channels
                .Select(channel => channel.Id)
                .Distinct(StringComparer.Ordinal) ?? Enumerable.Empty<string>())
            {
                await _channelRepository.UpdateSubscriptionAsync(channelId, id);
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

        private async Task RemoveChannelRecoverablyAsync(Guid listId, string channelId)
        {
            var listIdText = listId.ToString("D");
            var existingList = await ReadListAsync(listId);
            if (existingList == null)
            {
                await _channelRepository.RepairSubscriptionFromListTruthAsync(
                    channelId,
                    listId,
                    CancellationToken.None);
                return;
            }

            await EnsureLifecycleForExistingListAsync(existingList);
            var owner = CreateMutationOwner();
            var edge = await _recoveryStore.ActivateCandidateAsync(
                listIdText,
                channelId,
                owner,
                CancellationToken.None);
            var committedVersion = await UpdateRecoverableMembershipAsync(
                listId,
                document =>
                {
                    var remaining = document.Channels
                        .Where(channel => !string.Equals(
                            channel.Id,
                            channelId,
                            StringComparison.Ordinal))
                        .ToArray();
                    if (remaining.Length == document.Channels.Count)
                    {
                        return false;
                    }

                    document.Channels = remaining;
                    return true;
                },
                edge,
                owner);
            await _channelRepository.RepairSubscriptionFromListTruthAsync(
                channelId,
                listId,
                CancellationToken.None);
            var lifecycle = await _recoveryStore.ReadLifecycleAsync(
                listIdText,
                CancellationToken.None);
            edge = await _recoveryStore.ReadEdgeAsync(
                listIdText,
                edge.Id,
                CancellationToken.None);
            if (edge != null && lifecycle != null)
            {
                await _recoveryStore.RetireEdgeAsync(
                    edge,
                    lifecycle,
                    committedVersion,
                    channelId,
                    edge.Id,
                    CancellationToken.None,
                    revalidateAuthoritativeAbsenceAsync: cancellationToken =>
                        IsMembershipAbsentAtVersionAsync(
                            listId,
                            channelId,
                            committedVersion,
                            cancellationToken));
            }

            await ClearMembershipPendingAsync(listId, committedVersion);
            await ForceRecoveryAsync();
        }

        private async Task<long> UpdateRecoverableMembershipAsync(
            Guid listId,
            Func<CosmosListDocument, bool> update,
            CosmosRecoveryEdgeDocument ownedEdge,
            string owner)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(listId);
                if (document == null)
                {
                    return 0;
                }

                var changed = update(document);
                if (changed)
                {
                    document.MembershipVersion++;
                }

                document.MembershipRecoveryStartedAt ??= _clock.UtcNow;
                document.MembershipRecoveryPending = true;
                document.MembershipRecoveryDueAt = _clock.UtcNow;
                document.Ttl = CosmosDocumentMapper.GetTtlSeconds(
                    document.ExpiredAfter,
                    _clock.UtcNow);
                var boundedDocument = CosmosListProjectionPolicy.CreateBoundedCopy(
                    document,
                    _clock.UtcNow);
                await VerifyOwnedMutationLeaseAsync(
                    listId.ToString("D"),
                    ownedEdge,
                    owner);
                try
                {
                    await _lists.ReplaceItemAsync(
                        boundedDocument,
                        boundedDocument.Id,
                        new PartitionKey(boundedDocument.Id),
                        new ItemRequestOptions { IfMatchEtag = boundedDocument.ETag });
                    return document.MembershipVersion;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "CommitMembership",
                        retry: true);
                }
            }

            throw new InvalidOperationException("Membership write conflicted twice.");
        }

        private async Task VerifyOwnedMutationLeaseAsync(
            string listId,
            CosmosRecoveryEdgeDocument ownedEdge,
            string owner)
        {
            var edge = await _recoveryStore.ReadEdgeAsync(
                listId,
                ownedEdge.Id,
                CancellationToken.None);
            if (edge == null
                || edge.Generation != ownedEdge.Generation
                || !string.Equals(edge.Owner, owner, StringComparison.Ordinal)
                || edge.LeaseUntil <= _clock.UtcNow.Add(
                    _recoveryOptions.MutationCommitSafetyWindow))
            {
                throw new RecoveryLeaseUnavailableException(
                    "The membership mutation lost its exact recovery generation or commit lease.");
            }
        }

        private static string CreateMutationOwner()
        {
            return $"request:{Environment.ProcessId}:{Guid.NewGuid():N}";
        }

        private async Task<bool> IsMembershipAbsentAtVersionAsync(
            Guid listId,
            string channelId,
            long membershipVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = await ReadListAsync(listId);
            return list != null
                && list.MembershipVersion == membershipVersion
                && !list.Channels.Any(channel => string.Equals(
                    channel.Id,
                    channelId,
                    StringComparison.Ordinal));
        }

        private async Task EnsureLifecycleForExistingListAsync(
            CosmosListDocument list,
            bool reportLifecycleSideEffects = false)
        {
            var lifecycle = await _recoveryStore.ReadLifecycleAsync(
                list.Id,
                CancellationToken.None);
            if (lifecycle == null)
            {
                await _recoveryStore.CreateLifecycleAsync(
                    list.Id,
                    list.ExpiredAfter,
                    CancellationToken.None);
            }

            // A prior bootstrap can have stopped after creating the lifecycle or
            // after any individual edge. Reconcile every authoritative member on
            // every retry so legacy lists cannot remain only partly protected.
            foreach (var channel in list.Channels
                .Where(value => value != null)
                .DistinctBy(value => value.Id, StringComparer.Ordinal))
            {
                var edgeId = CosmosRecoveryStore.GetEdgeId(channel.Id);
                if (await _recoveryStore.ReadEdgeAsync(
                    list.Id,
                    edgeId,
                    CancellationToken.None) != null)
                {
                    continue;
                }

                var owner = CreateMutationOwner();
                var edge = await _recoveryStore.ActivateCandidateAsync(
                    list.Id,
                    channel.Id,
                    owner,
                    CancellationToken.None);
                await _recoveryStore.MarkTrackedAsync(
                    edge,
                    list.MembershipVersion,
                    CancellationToken.None);
                if (reportLifecycleSideEffects
                    && _interleavingHooks?.AfterLifecycleSideEffectAsync != null)
                {
                    await _interleavingHooks.AfterLifecycleSideEffectAsync(
                        list.Id,
                        $"EdgeSeeded:{channel.Id}");
                }
            }
        }

        private async Task ClearMembershipPendingAsync(Guid listId, long membershipVersion)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var document = await ReadListAsync(listId);
                if (document == null
                    || document.MembershipVersion != membershipVersion
                    || !document.MembershipRecoveryPending)
                {
                    return;
                }

                document.MembershipRecoveryPending = false;
                document.MembershipRecoveryDueAt = null;
                document.MembershipRecoveryStartedAt = null;
                document.MembershipRecoveryAttempt = 0;
                document.MembershipRecoveryPoison = false;
                document.MembershipRecoveryLastErrorClass = null;
                try
                {
                    await ReplaceAsync(document);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "ClearMembershipPending",
                        retry: true);
                }
            }
        }

        private async Task ForceRecoveryAsync()
        {
            if (_workerStateStore == null)
            {
                return;
            }

            await _workerStateStore.ForceConsistencyRecoveryAsync(CancellationToken.None);
            _wakeSignal?.Pulse();
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
                    CosmosRecoveryTelemetry.RecordConflict(
                        "ListRepository",
                        "RenewLifecycle",
                        retry: true);
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
