using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosConsistencyRecoveryService : IConsistencyRecoveryService
    {
        private const int MaxWriteAttempts = 2;
        private const string SystemPartition = "__system";
        private static readonly string[] WorkKinds =
        {
            "Membership",
            "Projection",
            "EdgeDue",
            "LifecycleDue"
        };
        private static readonly Meter RecoveryMeter = new(
            "youtubed.cosmos.recovery",
            "1.0.0");
        private static readonly Counter<long> AttemptCounter =
            RecoveryMeter.CreateCounter<long>("recovery.attempts");
        private static readonly Counter<long> SuccessCounter =
            RecoveryMeter.CreateCounter<long>("recovery.successes");
        private static readonly Counter<long> FailureCounter =
            RecoveryMeter.CreateCounter<long>("recovery.failures");
        private static readonly Counter<long> PoisonCounter =
            RecoveryMeter.CreateCounter<long>("recovery.poison");
        private static readonly Counter<long> RetryCounter =
            RecoveryMeter.CreateCounter<long>("recovery.retries");
        private static readonly Counter<long> ConflictCounter =
            RecoveryMeter.CreateCounter<long>("recovery.etag_conflicts");
        private static readonly Counter<long> LeaseTakeoverCounter =
            RecoveryMeter.CreateCounter<long>("recovery.lease_takeovers");
        private static readonly Counter<long> MissingListCounter =
            RecoveryMeter.CreateCounter<long>("recovery.list_not_found");
        private static readonly Counter<double> RequestChargeCounter =
            RecoveryMeter.CreateCounter<double>("recovery.request_charge");
        private static readonly Histogram<double> PassDuration =
            RecoveryMeter.CreateHistogram<double>("recovery.pass.duration", "ms");
        private static readonly Histogram<double> OldestPendingAge =
            RecoveryMeter.CreateHistogram<double>("recovery.pending.oldest_age", "ms");

        private readonly Container _lists;
        private readonly Container _channels;
        private readonly Container _recovery;
        private readonly IAppClock _clock;
        private readonly CosmosRecoveryOptions _options;
        private readonly CosmosRecoveryStore _store;
        private readonly CosmosChannelRepository _channelRepository;
        private readonly CosmosListProjectionRepository _projectionRepository;
        private readonly ILogger<CosmosConsistencyRecoveryService> _logger;
        private readonly string _owner =
            $"recovery:{Environment.ProcessId}:{Guid.NewGuid():N}";
        private readonly CosmosRecoveryInterleavingHooks _interleavingHooks;

        public CosmosConsistencyRecoveryService(
            Container lists,
            Container channels,
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions options,
            ILogger<CosmosConsistencyRecoveryService> logger)
            : this(lists, channels, recovery, clock, options, logger, null)
        {
        }

        internal CosmosConsistencyRecoveryService(
            Container lists,
            Container channels,
            Container recovery,
            IAppClock clock,
            CosmosRecoveryOptions options,
            ILogger<CosmosConsistencyRecoveryService> logger,
            CosmosRecoveryInterleavingHooks interleavingHooks)
        {
            _lists = lists ?? throw new ArgumentNullException(nameof(lists));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
            _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _interleavingHooks = interleavingHooks;
            _store = new CosmosRecoveryStore(recovery, clock, options);
            _channelRepository = new CosmosChannelRepository(
                channels,
                lists,
                recovery,
                clock,
                options);
            _projectionRepository = new CosmosListProjectionRepository(
                lists,
                channels,
                recovery,
                clock,
                options,
                interleavingHooks);
        }

        public async Task<ConsistencyRecoveryPassResult> RecoverAsync(
            ConsistencyRecoveryPassBudget budget,
            CancellationToken cancellationToken)
        {
            budget.Validate();
            using var requestChargeScope = CosmosRequestChargeScope.Begin();
            var claimed = 0;
            var succeeded = 0;
            var failed = 0;
            var poison = 0;
            var charge = 0d;
            var hasMore = false;
            var pendingItems = 0;
            DateTimeOffset? oldestPendingAt = null;
            var consecutiveEmptyTickets = 0;
            var startedAt = _clock.UtcNow;
            var admission = new RecoveryAdmissionBudget(
                budget.MaxItems,
                budget.RuSchedulingBudget,
                requestChargeScope);

            while (admission.CanSchedule)
            {
                var ticket = await TakeTicketAsync(cancellationToken);
                if (ticket == null)
                {
                    hasMore = true;
                    break;
                }

                var available = Math.Min(
                    budget.PageSize,
                    admission.RemainingItems);
                var page = ticket switch
                {
                    "Membership" => await ProcessMembershipPageAsync(
                        available,
                        admission,
                        cancellationToken),
                    "Projection" => await ProcessProjectionPageAsync(
                        available,
                        admission,
                        cancellationToken),
                    "EdgeDue" => await ProcessEdgePageAsync(
                        available,
                        admission,
                        cancellationToken),
                    "LifecycleDue" => await ObserveLifecyclePageAsync(
                        available,
                        admission,
                        cancellationToken),
                    _ => throw new InvalidOperationException(
                        $"Unsupported recovery ticket '{ticket}'.")
                };
                claimed += page.Claimed;
                succeeded += page.Succeeded;
                failed += page.Failed;
                poison += page.Poison;
                charge += page.RequestCharge;
                hasMore |= page.HasMore;
                pendingItems += page.PendingItems;
                if (page.OldestPendingAt.HasValue
                    && (!oldestPendingAt.HasValue
                        || page.OldestPendingAt < oldestPendingAt))
                {
                    oldestPendingAt = page.OldestPendingAt;
                }

                if (page.FoundEligibleWork)
                {
                    consecutiveEmptyTickets = 0;
                }
                else
                {
                    consecutiveEmptyTickets++;
                    if (consecutiveEmptyTickets >= WorkKinds.Length)
                    {
                        break;
                    }
                }

                if (!admission.CanSchedule)
                {
                    hasMore = true;
                    break;
                }
            }

            var result = new ConsistencyRecoveryPassResult(
                admission.ProcessedItems,
                claimed,
                succeeded,
                failed,
                poison,
                GetMeasuredCharge(charge, requestChargeScope),
                hasMore,
                hasMore ? _clock.UtcNow : _clock.UtcNow.Add(_options.PollInterval));
            AttemptCounter.Add(result.Claimed);
            SuccessCounter.Add(result.Succeeded);
            FailureCounter.Add(result.Failed);
            PoisonCounter.Add(result.Poison);
            RequestChargeCounter.Add(result.RequestCharge);
            PassDuration.Record((_clock.UtcNow - startedAt).TotalMilliseconds);
            var oldestPendingAgeMs = oldestPendingAt.HasValue
                ? Math.Max(0, (_clock.UtcNow - oldestPendingAt.Value).TotalMilliseconds)
                : 0;
            if (oldestPendingAt.HasValue)
            {
                OldestPendingAge.Record(oldestPendingAgeMs);
            }
            _logger.LogInformation(
                "Cosmos consistency recovery pass completed. Examined={Examined}; Claimed={Claimed}; Succeeded={Succeeded}; Failed={Failed}; Poison={Poison}; PendingItems={PendingItems}; OldestPendingAgeMs={OldestPendingAgeMs}; RequestCharge={RequestCharge}; HasMoreEligibleWork={HasMoreEligibleWork}; DurationMs={DurationMs}.",
                result.Examined,
                result.Claimed,
                result.Succeeded,
                result.Failed,
                result.Poison,
                pendingItems,
                oldestPendingAgeMs,
                result.RequestCharge,
                result.HasMoreEligibleWork,
                (_clock.UtcNow - startedAt).TotalMilliseconds);
            return result;
        }

        private static double GetMeasuredCharge(
            double queryChargeFallback,
            CosmosRequestChargeScope scope)
        {
            return scope.RequestCharge > 0
                ? scope.RequestCharge
                : queryChargeFallback;
        }

        private async Task<PageResult> ProcessMembershipPageAsync(
            int take,
            RecoveryAdmissionBudget admission,
            CancellationToken cancellationToken)
        {
            var cursor = await GetOrCreateCursorAsync("Membership", cancellationToken);
            var query = CreateMembershipDueQuery(cursor);
            using var iterator = _lists.GetItemQueryIterator<MembershipWorkItem>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
            var response = await iterator.ReadNextAsync(cancellationToken);
            admission.RecordQueryCharge(response.RequestCharge);
            var items = response.Take(1).ToArray();
            CosmosRecoveryTelemetry.RecordPending("Membership", items.Length);

            var page = new PageResult
            {
                RequestCharge = response.RequestCharge,
                HasMore = items.Length == 1,
                FoundEligibleWork = items.Length > 0,
                PendingItems = items.Length,
                OldestPendingAt = items.FirstOrDefault()?.MembershipRecoveryDueAt
            };
            if (items.Length == 0)
            {
                await AdvanceCursorAsync(
                    cursor,
                    null,
                    null,
                    null,
                    wrap: true,
                    cancellationToken);
                return page;
            }

            if (!admission.CanSchedule)
            {
                return page;
            }

            foreach (var item in items)
            {
                try
                {
                    var outcome = await RecoverMembershipAsync(
                        item,
                        take,
                        admission,
                        cancellationToken);
                    page.Claimed += outcome.Claimed ? 1 : 0;
                    page.Succeeded += outcome.Succeeded ? 1 : 0;
                    page.HasMore |= outcome.HasMore;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    || !cancellationToken.IsCancellationRequested)
                {
                    var failure = await FailMembershipAsync(
                        item,
                        exception,
                        cancellationToken);
                    page.Failed++;
                    page.Poison += failure.Poison ? 1 : 0;
                    if (failure.Poison)
                    {
                        _logger.LogError(
                            exception,
                            "Cosmos recovery work failed. WorkKind={WorkKind}; ListId={ListId}; ObservedVersion={ObservedVersion}; Attempt={Attempt}; Poison={Poison}; NextAttemptAt={NextAttemptAt}; ErrorClass={ErrorClass}; Result={Result}.",
                            "Membership",
                            item.Id,
                            item.MembershipVersion,
                            failure.Attempt,
                            true,
                            failure.NextAttemptAt,
                            exception.GetType().Name,
                            "Poison");
                    }
                    else
                    {
                        _logger.LogWarning(
                            exception,
                            "Cosmos recovery work failed. WorkKind={WorkKind}; ListId={ListId}; ObservedVersion={ObservedVersion}; Attempt={Attempt}; Poison={Poison}; NextAttemptAt={NextAttemptAt}; ErrorClass={ErrorClass}; Result={Result}.",
                            "Membership",
                            item.Id,
                            item.MembershipVersion,
                            failure.Attempt,
                            false,
                            failure.NextAttemptAt,
                            exception.GetType().Name,
                            "Retry");
                    }
                }
            }

            await AdvanceCursorAsync(
                cursor,
                items[0].MembershipRecoveryDueAt,
                null,
                items[0].Id,
                wrap: false,
                cancellationToken);

            return page;
        }

        private async Task<WorkOutcome> RecoverMembershipAsync(
            MembershipWorkItem item,
            int take,
            RecoveryAdmissionBudget admission,
            CancellationToken cancellationToken)
        {
            var list = await ReadListAsync(item.Id, cancellationToken);
            if (list == null
                || !list.MembershipRecoveryPending
                || list.MembershipVersion != item.MembershipVersion)
            {
                return new WorkOutcome(false, false, false, 0);
            }

            if (_interleavingHooks?.BeforeMembershipWorkAsync != null)
            {
                await _interleavingHooks.BeforeMembershipWorkAsync(list.Id);
            }

            await _store.CreateLifecycleAsync(
                list.Id,
                list.ExpiredAfter,
                cancellationToken);
            var lifecycle = await _store.ClaimLifecycleAsync(
                list.Id,
                _owner,
                cancellationToken);
            if (lifecycle == null)
            {
                return new WorkOutcome(false, false, true, 0);
            }

            var expectedGeneration = lifecycle.MembershipTraversalEdgeGeneration;
            var restart = lifecycle.MembershipVersionBeingRepaired != list.MembershipVersion
                || (expectedGeneration.HasValue
                    && expectedGeneration.Value != lifecycle.EdgeGeneration);
            if (restart)
            {
                lifecycle = await _store.SaveMembershipCheckpointAsync(
                    lifecycle,
                    list.MembershipVersion,
                    lifecycle.EdgeGeneration,
                    null,
                    null,
                    releaseLease: false,
                    cancellationToken);
            }
            else if (!expectedGeneration.HasValue)
            {
                lifecycle = await _store.SaveMembershipCheckpointAsync(
                    lifecycle,
                    list.MembershipVersion,
                    lifecycle.EdgeGeneration,
                    lifecycle.MembershipEdgeAfterChannelId,
                    lifecycle.MembershipEdgeAfterId,
                    releaseLease: false,
                    cancellationToken);
            }

            var edges = await _store.QueryMembershipEdgesAsync(
                list.Id,
                lifecycle.MembershipEdgeAfterChannelId,
                lifecycle.MembershipEdgeAfterId,
                take,
                cancellationToken);
            foreach (var edge in edges)
            {
                if (!admission.TryAdmitItem())
                {
                    await _store.SaveMembershipCheckpointAsync(
                        lifecycle,
                        list.MembershipVersion,
                        lifecycle.EdgeGeneration,
                        lifecycle.MembershipEdgeAfterChannelId,
                        lifecycle.MembershipEdgeAfterId,
                        releaseLease: true,
                        cancellationToken);
                    return new WorkOutcome(true, false, true, 0);
                }

                if (_interleavingHooks?.BeforeMembershipEdgeAsync != null)
                {
                    await _interleavingHooks.BeforeMembershipEdgeAsync(edge);
                }

                if (edge.LeaseUntil > _clock.UtcNow
                    && !string.Equals(edge.Owner, _owner, StringComparison.Ordinal))
                {
                    await _store.SaveMembershipCheckpointAsync(
                        lifecycle,
                        list.MembershipVersion,
                        lifecycle.EdgeGeneration,
                        lifecycle.MembershipEdgeAfterChannelId,
                        lifecycle.MembershipEdgeAfterId,
                        releaseLease: true,
                        cancellationToken);
                    return new WorkOutcome(true, false, true, edges.Count);
                }

                var currentLifecycle = await _store.ReadLifecycleAsync(
                    list.Id,
                    cancellationToken);
                if (currentLifecycle == null
                    || currentLifecycle.EdgeGeneration != lifecycle.EdgeGeneration)
                {
                    if (currentLifecycle != null)
                    {
                        await _store.SaveMembershipCheckpointAsync(
                            currentLifecycle,
                            list.MembershipVersion,
                            currentLifecycle.EdgeGeneration,
                            null,
                            null,
                            releaseLease: true,
                            cancellationToken);
                    }

                    return new WorkOutcome(true, false, true, edges.Count);
                }

                var present = list.Channels.Any(channel => string.Equals(
                    channel.Id,
                    edge.ChannelId,
                    StringComparison.Ordinal));
                await _channelRepository.RepairSubscriptionFromListTruthAsync(
                    edge.ChannelId,
                    Guid.Parse(list.Id),
                    cancellationToken);
                await EnsureChannelConvergedAsync(
                    edge.ChannelId,
                    list.Id,
                    present,
                    cancellationToken);
                if (present)
                {
                    await _store.MarkTrackedAsync(
                        edge,
                        list.MembershipVersion,
                        cancellationToken);
                    lifecycle = await _store.SaveMembershipCheckpointAsync(
                        lifecycle,
                        list.MembershipVersion,
                        lifecycle.EdgeGeneration,
                        edge.ChannelId,
                        edge.Id,
                        releaseLease: false,
                        cancellationToken);
                }
                else
                {
                    var retirement = await _store.RetireEdgeAsync(
                        edge,
                        lifecycle,
                        list.MembershipVersion,
                        edge.ChannelId,
                        edge.Id,
                        cancellationToken,
                        adoptMembershipCheckpoint: true,
                        revalidateAuthoritativeAbsenceAsync: async retryToken =>
                        {
                            var currentList = await ReadListAsync(
                                list.Id,
                                retryToken);
                            return currentList != null
                                && currentList.MembershipVersion
                                    == list.MembershipVersion
                                && !currentList.Channels.Any(channel =>
                                    string.Equals(
                                        channel.Id,
                                        edge.ChannelId,
                                        StringComparison.Ordinal));
                        });
                    if (!retirement.Retired)
                    {
                        return new WorkOutcome(true, false, true, edges.Count);
                    }

                    lifecycle.EdgeGeneration = retirement.EdgeGeneration;
                    lifecycle.MembershipTraversalEdgeGeneration = retirement.EdgeGeneration;
                    lifecycle.MembershipEdgeAfterChannelId = edge.ChannelId;
                    lifecycle.MembershipEdgeAfterId = edge.Id;
                }
            }

            if (edges.Count == take)
            {
                await _store.SaveMembershipCheckpointAsync(
                    lifecycle,
                    list.MembershipVersion,
                    lifecycle.EdgeGeneration,
                    lifecycle.MembershipEdgeAfterChannelId,
                    lifecycle.MembershipEdgeAfterId,
                    releaseLease: true,
                    cancellationToken);
                return new WorkOutcome(true, false, true, edges.Count);
            }

            if (edges.Count == 0 && !admission.TryAdmitItem())
            {
                await _store.SaveMembershipCheckpointAsync(
                    lifecycle,
                    list.MembershipVersion,
                    lifecycle.EdgeGeneration,
                    lifecycle.MembershipEdgeAfterChannelId,
                    lifecycle.MembershipEdgeAfterId,
                    releaseLease: true,
                    cancellationToken);
                return new WorkOutcome(true, false, true, 0);
            }

            var cleared = await ClearListPendingAsync(
                list.Id,
                list.MembershipVersion,
                cancellationToken);
            await _store.SaveMembershipCheckpointAsync(
                lifecycle,
                list.MembershipVersion,
                lifecycle.EdgeGeneration,
                null,
                null,
                releaseLease: true,
                cancellationToken);
            return new WorkOutcome(true, cleared, !cleared, edges.Count);
        }

        private async Task<PageResult> ProcessProjectionPageAsync(
            int take,
            RecoveryAdmissionBudget admission,
            CancellationToken cancellationToken)
        {
            var cursor = await GetOrCreateCursorAsync("Projection", cancellationToken);
            var query = CreateProjectionDueQuery(cursor);
            using var iterator = _channels.GetItemQueryIterator<ProjectionWorkItem>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
            var response = await iterator.ReadNextAsync(cancellationToken);
            admission.RecordQueryCharge(response.RequestCharge);
            var items = response.Take(1).ToArray();
            CosmosRecoveryTelemetry.RecordPending("Projection", items.Length);

            var page = new PageResult
            {
                RequestCharge = response.RequestCharge,
                HasMore = items.Length == 1,
                FoundEligibleWork = items.Length > 0,
                PendingItems = items.Length,
                OldestPendingAt = items.FirstOrDefault()?.ProjectionRecoveryDueAt
            };
            if (items.Length == 0)
            {
                await AdvanceCursorAsync(
                    cursor,
                    null,
                    null,
                    null,
                    wrap: true,
                    cancellationToken);
                return page;
            }

            if (!admission.CanSchedule)
            {
                return page;
            }

            foreach (var item in items)
            {
                try
                {
                    page.Claimed++;
                    if (_interleavingHooks?.BeforeProjectionWorkAsync != null)
                    {
                        await _interleavingHooks.BeforeProjectionWorkAsync(item.Id);
                    }

                    var result = await _projectionRepository.RecoverPendingProjectionAsync(
                        item.Id,
                        take,
                        admission.TryAdmitItem,
                        cancellationToken);
                    page.Succeeded += result.HasMore ? 0 : 1;
                    if (!result.HasMore)
                    {
                        CosmosRecoveryTelemetry.RecordConvergence(
                            "Projection",
                            _clock.UtcNow,
                            item.ProjectionRecoveryStartedAt);
                        _logger.LogInformation(
                            "Cosmos recovery work converged. WorkKind={WorkKind}; ChannelId={ChannelId}; ProjectionVersion={ProjectionVersion}; ConvergenceLatencyMs={ConvergenceLatencyMs}; Result={Result}.",
                            "Projection",
                            item.Id,
                            item.ProjectionVersion,
                            item.ProjectionRecoveryStartedAt.HasValue
                                ? (_clock.UtcNow
                                    - item.ProjectionRecoveryStartedAt.Value)
                                    .TotalMilliseconds
                                : 0,
                            "Succeeded");
                    }
                    page.Examined += Math.Max(1, result.Examined);
                    page.HasMore |= result.HasMore;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    || !cancellationToken.IsCancellationRequested)
                {
                    var failure = await FailProjectionAsync(
                        item,
                        exception,
                        cancellationToken);
                    page.Failed++;
                    page.Poison += failure.Poison ? 1 : 0;
                    if (failure.Poison)
                    {
                        _logger.LogError(
                            exception,
                            "Cosmos recovery work failed. WorkKind={WorkKind}; ChannelId={ChannelId}; ProjectionVersion={ProjectionVersion}; SubscriptionGeneration={SubscriptionGeneration}; Attempt={Attempt}; Poison={Poison}; NextAttemptAt={NextAttemptAt}; ErrorClass={ErrorClass}; Result={Result}.",
                            "Projection",
                            item.Id,
                            item.ProjectionVersion,
                            item.SubscriptionGeneration,
                            failure.Attempt,
                            true,
                            failure.NextAttemptAt,
                            exception.GetType().Name,
                            "Poison");
                    }
                    else
                    {
                        _logger.LogWarning(
                            exception,
                            "Cosmos recovery work failed. WorkKind={WorkKind}; ChannelId={ChannelId}; ProjectionVersion={ProjectionVersion}; SubscriptionGeneration={SubscriptionGeneration}; Attempt={Attempt}; Poison={Poison}; NextAttemptAt={NextAttemptAt}; ErrorClass={ErrorClass}; Result={Result}.",
                            "Projection",
                            item.Id,
                            item.ProjectionVersion,
                            item.SubscriptionGeneration,
                            failure.Attempt,
                            false,
                            failure.NextAttemptAt,
                            exception.GetType().Name,
                            "Retry");
                    }
                }
            }

            await AdvanceCursorAsync(
                cursor,
                items[0].ProjectionRecoveryDueAt,
                null,
                items[0].Id,
                wrap: false,
                cancellationToken);

            return page;
        }

        private async Task<PageResult> ProcessEdgePageAsync(
            int take,
            RecoveryAdmissionBudget admission,
            CancellationToken cancellationToken)
        {
            var cursor = await GetOrCreateCursorAsync("EdgeDue", cancellationToken);
            var query = CreateEdgeDueQuery(cursor);
            using var iterator = _recovery.GetItemQueryIterator<EdgeWorkItem>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = take });
            var response = await iterator.ReadNextAsync(cancellationToken);
            admission.RecordQueryCharge(response.RequestCharge);
            var items = response.Take(take).ToArray();
            CosmosRecoveryTelemetry.RecordPending("EdgeDue", items.Length);

            var page = new PageResult
            {
                RequestCharge = response.RequestCharge,
                HasMore = items.Length == take,
                FoundEligibleWork = items.Length > 0,
                PendingItems = items.Length,
                OldestPendingAt = items.FirstOrDefault()?.NextAttemptAt
            };
            EdgeWorkItem lastAdmitted = null;
            foreach (var item in items)
            {
                if (!admission.TryAdmitItem())
                {
                    page.HasMore = true;
                    break;
                }

                lastAdmitted = item;
                page.Examined++;
                var edge = await _store.ClaimEdgeAsync(
                    item.ListId,
                    item.Id,
                    _owner,
                    cancellationToken);
                if (edge == null)
                {
                    continue;
                }

                page.Claimed++;
                if (edge.LeaseTakenOver)
                {
                    LeaseTakeoverCounter.Add(1);
                    _logger.LogInformation(
                        "Cosmos recovery lease taken over. WorkKind={WorkKind}; ListId={ListId}; ChannelId={ChannelId}; Generation={Generation}; Result={Result}.",
                        "EdgeDue",
                        edge.ListId,
                        edge.ChannelId,
                        edge.Generation,
                        "LeaseTakenOver");
                }

                try
                {
                    var list = await ReadListAsync(item.ListId, cancellationToken);
                    if (list == null)
                    {
                        MissingListCounter.Add(1);
                        _logger.LogWarning(
                            "Cosmos recovery list was not found. WorkKind={WorkKind}; ListId={ListId}; ChannelId={ChannelId}; Result={Result}.",
                            "EdgeDue",
                            edge.ListId,
                            edge.ChannelId,
                            "ListNotFound");
                    }

                    var present = list?.Channels.Any(channel => string.Equals(
                        channel.Id,
                        edge.ChannelId,
                        StringComparison.Ordinal)) == true;
                    await _channelRepository.RepairSubscriptionFromListTruthAsync(
                        edge.ChannelId,
                        Guid.Parse(edge.ListId),
                        cancellationToken);
                    await EnsureChannelConvergedAsync(
                        edge.ChannelId,
                        edge.ListId,
                        present,
                        cancellationToken);
                    if (present)
                    {
                        await _store.MarkTrackedAsync(
                            edge,
                            list.MembershipVersion,
                            cancellationToken);
                    }
                    else
                    {
                        var lifecycle = await _store.ReadLifecycleAsync(
                            edge.ListId,
                            cancellationToken);
                        if (lifecycle != null)
                        {
                            await _store.RetireEdgeAsync(
                                edge,
                                lifecycle,
                                list?.MembershipVersion ?? 0,
                                lifecycle.MembershipEdgeAfterChannelId,
                                lifecycle.MembershipEdgeAfterId,
                                cancellationToken,
                                revalidateAuthoritativeAbsenceAsync:
                                    async retryToken =>
                                    {
                                        var currentList = await ReadListAsync(
                                            edge.ListId,
                                            retryToken);
                                        return currentList == null
                                            || !currentList.Channels.Any(channel =>
                                                string.Equals(
                                                    channel.Id,
                                                    edge.ChannelId,
                                                    StringComparison.Ordinal));
                                    });
                        }
                    }

                    page.Succeeded++;
                    _logger.LogInformation(
                        "Cosmos recovery edge repaired. WorkKind={WorkKind}; ListId={ListId}; ChannelId={ChannelId}; Attempt={Attempt}; Result={Result}.",
                        "EdgeDue",
                        edge.ListId,
                        edge.ChannelId,
                        edge.Attempt,
                        present ? "Tracked" : "Retired");
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException
                    || !cancellationToken.IsCancellationRequested)
                {
                    await _store.FailEdgeAsync(edge, exception, cancellationToken);
                    page.Failed++;
                    if (edge.Attempt >= _options.PoisonAttemptCount)
                    {
                        page.Poison++;
                        _logger.LogError(
                            exception,
                            "Cosmos recovery edge is poison. WorkKind={WorkKind}; ListId={ListId}; ChannelId={ChannelId}; Attempt={Attempt}; Result={Result}.",
                            "EdgeDue",
                            edge.ListId,
                            edge.ChannelId,
                            edge.Attempt,
                            "Poison");
                    }
                }
            }

            await AdvanceCursorAsync(
                cursor,
                lastAdmitted?.NextAttemptAt,
                lastAdmitted?.ListId,
                lastAdmitted?.Id,
                items.Length == 0,
                cancellationToken);
            return page;
        }

        private async Task<PageResult> ObserveLifecyclePageAsync(
            int take,
            RecoveryAdmissionBudget admission,
            CancellationToken cancellationToken)
        {
            // Task 2120 owns lifecycle deadline/deletion processing. Task 2110 owns
            // the indexed queue, cursor, fair ticket, and shared claim substrate.
            var cursor = await GetOrCreateCursorAsync("LifecycleDue", cancellationToken);
            var query = CreateLifecycleDueQuery(cursor);
            using var iterator = _recovery.GetItemQueryIterator<LifecycleWorkItem>(
                query,
                requestOptions: new QueryRequestOptions { MaxItemCount = take });
            var response = await iterator.ReadNextAsync(cancellationToken);
            admission.RecordQueryCharge(response.RequestCharge);
            var items = response.Take(take).ToArray();
            CosmosRecoveryTelemetry.RecordPending("LifecycleDue", items.Length);
            var admittedItems = 0;
            LifecycleWorkItem lastAdmitted = null;
            foreach (var _ in items)
            {
                if (!admission.TryAdmitItem())
                {
                    break;
                }

                admittedItems++;
                lastAdmitted = _;
            }
            await AdvanceCursorAsync(
                cursor,
                lastAdmitted?.NextCheckAt,
                lastAdmitted?.ListId,
                lastAdmitted?.Id,
                items.Length == 0,
                cancellationToken);
            return new PageResult
            {
                Examined = admittedItems,
                RequestCharge = response.RequestCharge,
                HasMore = admittedItems < items.Length,
                FoundEligibleWork = items.Length > 0,
                PendingItems = items.Length,
                OldestPendingAt = items.FirstOrDefault()?.NextCheckAt
            };
        }

        private async Task<string> TakeTicketAsync(CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var cursor = await ReadTicketAsync(cancellationToken);
                if (cursor == null)
                {
                    cursor = new CosmosRecoveryTicketCursorDocument
                    {
                        UpdatedAt = _clock.UtcNow
                    };
                    try
                    {
                        var created = await _recovery.CreateItemAsync(
                            cursor,
                            new PartitionKey(SystemPartition),
                            cancellationToken: cancellationToken);
                        cursor = created.Resource;
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.Conflict)
                    {
                        if (attempt + 1 < MaxWriteAttempts)
                        {
                            RecordOptimisticRetry("Ticket", cursor.Id);
                            continue;
                        }

                        RecordContentionDeferred("Ticket", cursor.Id);
                        return null;
                    }
                }

                var ticket = WorkKinds.Contains(cursor.NextStartingKind, StringComparer.Ordinal)
                    ? cursor.NextStartingKind
                    : WorkKinds[0];
                cursor.NextStartingKind = WorkKinds[
                    (Array.IndexOf(WorkKinds, ticket) + 1) % WorkKinds.Length];
                cursor.RotationGeneration++;
                cursor.UpdatedAt = _clock.UtcNow;
                try
                {
                    await _recovery.ReplaceItemAsync(
                        cursor,
                        cursor.Id,
                        new PartitionKey(SystemPartition),
                        new ItemRequestOptions { IfMatchEtag = cursor.ETag },
                        cancellationToken);
                    return ticket;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt + 1 < MaxWriteAttempts)
                    {
                        RecordOptimisticRetry("Ticket", cursor.Id);
                        continue;
                    }

                    RecordContentionDeferred("Ticket", cursor.Id);
                    return null;
                }
            }

            return null;
        }

        private async Task<CosmosRecoveryCursorDocument> GetOrCreateCursorAsync(
            string workKind,
            CancellationToken cancellationToken)
        {
            var id = $"cursor:{ToKebabCase(workKind)}";
            try
            {
                var read = await _recovery.ReadItemAsync<CosmosRecoveryCursorDocument>(
                    id,
                    new PartitionKey(SystemPartition),
                    cancellationToken: cancellationToken);
                return read.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }

            var cursor = new CosmosRecoveryCursorDocument
            {
                Id = id,
                WorkKind = workKind,
                CycleNow = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow
            };
            try
            {
                var created = await _recovery.CreateItemAsync(
                    cursor,
                    new PartitionKey(SystemPartition),
                    cancellationToken: cancellationToken);
                return created.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                CosmosRecoveryTelemetry.RecordConflict(
                    "RecoveryService",
                    "CreateCursor",
                    retry: false);
                var read = await _recovery.ReadItemAsync<CosmosRecoveryCursorDocument>(
                    id,
                    new PartitionKey(SystemPartition),
                    cancellationToken: cancellationToken);
                return read.Resource;
            }
        }

        private async Task AdvanceCursorAsync(
            CosmosRecoveryCursorDocument cursor,
            DateTimeOffset? afterDueAt,
            string afterListId,
            string afterId,
            bool wrap,
            CancellationToken cancellationToken)
        {
            if (_interleavingHooks?.BeforeCursorAdvanceAsync != null)
            {
                await _interleavingHooks.BeforeCursorAdvanceAsync(cursor.WorkKind);
            }

            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                if (wrap)
                {
                    cursor.CycleNow = _clock.UtcNow;
                    cursor.CycleGeneration++;
                    cursor.AfterDueAt = null;
                    cursor.AfterListId = null;
                    cursor.AfterId = null;
                }
                else
                {
                    cursor.AfterDueAt = afterDueAt;
                    cursor.AfterListId = afterListId;
                    cursor.AfterId = afterId;
                }

                cursor.UpdatedAt = _clock.UtcNow;
                try
                {
                    await _recovery.ReplaceItemAsync(
                        cursor,
                        cursor.Id,
                        new PartitionKey(SystemPartition),
                        new ItemRequestOptions { IfMatchEtag = cursor.ETag },
                        cancellationToken);
                    return;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    RecordOptimisticRetry(cursor.WorkKind, cursor.Id);
                    var latest = await _recovery.ReadItemAsync<CosmosRecoveryCursorDocument>(
                        cursor.Id,
                        new PartitionKey(SystemPartition),
                        cancellationToken: cancellationToken);
                    cursor = latest.Resource;
                }
            }
        }

        private async Task<bool> ClearListPendingAsync(
            string listId,
            long membershipVersion,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var list = await ReadListAsync(listId, cancellationToken);
                if (list == null
                    || list.MembershipVersion != membershipVersion
                    || !list.MembershipRecoveryPending)
                {
                    return false;
                }

                list.MembershipRecoveryPending = false;
                list.MembershipRecoveryDueAt = null;
                var convergenceStartedAt = list.MembershipRecoveryStartedAt;
                list.MembershipRecoveryStartedAt = null;
                list.MembershipRecoveryAttempt = 0;
                list.MembershipRecoveryPoison = false;
                list.MembershipRecoveryLastErrorClass = null;
                var bounded = CosmosListProjectionPolicy.CreateBoundedCopy(list, _clock.UtcNow);
                try
                {
                    await _lists.ReplaceItemAsync(
                        bounded,
                        bounded.Id,
                        new PartitionKey(bounded.Id),
                        new ItemRequestOptions { IfMatchEtag = bounded.ETag },
                        cancellationToken);
                    _logger.LogInformation(
                        "Cosmos recovery work converged. WorkKind={WorkKind}; ListId={ListId}; ObservedVersion={ObservedVersion}; ConvergenceLatencyMs={ConvergenceLatencyMs}; Result={Result}.",
                        "Membership",
                        list.Id,
                        membershipVersion,
                        convergenceStartedAt.HasValue
                            ? (_clock.UtcNow - convergenceStartedAt.Value).TotalMilliseconds
                            : 0,
                        "Succeeded");
                    CosmosRecoveryTelemetry.RecordConvergence(
                        "Membership",
                        _clock.UtcNow,
                        convergenceStartedAt);
                    return true;
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.PreconditionFailed
                    && attempt + 1 < MaxWriteAttempts)
                {
                    RecordOptimisticRetry("Membership", list.Id);
                }
            }

            return false;
        }

        private async Task<FailureState> FailMembershipAsync(
            MembershipWorkItem work,
            Exception exception,
            CancellationToken cancellationToken)
        {
            for (var writeAttempt = 0; writeAttempt < MaxWriteAttempts; writeAttempt++)
            {
                var list = await ReadListAsync(work.Id, cancellationToken);
                if (list == null
                    || !list.MembershipRecoveryPending
                    || list.MembershipVersion != work.MembershipVersion)
                {
                    return FailureState.None;
                }

                list.MembershipRecoveryAttempt++;
                list.MembershipRecoveryPoison =
                    list.MembershipRecoveryAttempt >= _options.PoisonAttemptCount;
                list.MembershipRecoveryLastErrorClass = exception.GetType().Name;
                list.MembershipRecoveryDueAt = GetFailureDueAt(
                    list.MembershipRecoveryAttempt,
                    list.MembershipRecoveryPoison);
                var bounded = CosmosListProjectionPolicy.CreateBoundedCopy(
                    list,
                    _clock.UtcNow);
                try
                {
                    var response = await _lists.ReplaceItemAsync(
                        bounded,
                        bounded.Id,
                        new PartitionKey(bounded.Id),
                        new ItemRequestOptions { IfMatchEtag = bounded.ETag },
                        cancellationToken);
                    return new FailureState(
                        response.Resource.MembershipRecoveryAttempt,
                        response.Resource.MembershipRecoveryPoison,
                        response.Resource.MembershipRecoveryDueAt);
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.PreconditionFailed
                    && writeAttempt + 1 < MaxWriteAttempts)
                {
                    RecordOptimisticRetry("MembershipFailure", work.Id);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Membership failure state for list '{work.Id}' conflicted twice.");
        }

        private async Task<FailureState> FailProjectionAsync(
            ProjectionWorkItem work,
            Exception exception,
            CancellationToken cancellationToken)
        {
            for (var writeAttempt = 0; writeAttempt < MaxWriteAttempts; writeAttempt++)
            {
                CosmosChannelDocument channel;
                try
                {
                    channel = (await _channels.ReadItemAsync<CosmosChannelDocument>(
                        work.Id,
                        new PartitionKey(work.Id),
                        cancellationToken: cancellationToken)).Resource;
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.NotFound)
                {
                    return FailureState.None;
                }

                if (!channel.ProjectionRecoveryPending
                    || channel.ProjectionVersion != work.ProjectionVersion)
                {
                    return FailureState.None;
                }

                channel.ProjectionRecoveryAttempt++;
                channel.ProjectionRecoveryPoison =
                    channel.ProjectionRecoveryAttempt >= _options.PoisonAttemptCount;
                channel.ProjectionRecoveryLastErrorClass = exception.GetType().Name;
                channel.ProjectionRecoveryDueAt = GetFailureDueAt(
                    channel.ProjectionRecoveryAttempt,
                    channel.ProjectionRecoveryPoison);
                try
                {
                    var response = await _channels.ReplaceItemAsync(
                        channel,
                        channel.Id,
                        new PartitionKey(channel.Id),
                        new ItemRequestOptions { IfMatchEtag = channel.ETag },
                        cancellationToken);
                    return new FailureState(
                        response.Resource.ProjectionRecoveryAttempt,
                        response.Resource.ProjectionRecoveryPoison,
                        response.Resource.ProjectionRecoveryDueAt);
                }
                catch (CosmosException cosmosException) when (
                    cosmosException.StatusCode == HttpStatusCode.PreconditionFailed
                    && writeAttempt + 1 < MaxWriteAttempts)
                {
                    RecordOptimisticRetry("ProjectionFailure", work.Id);
                }
            }

            throw new CosmosRecoveryConflictException(
                $"Projection failure state for channel '{work.Id}' conflicted twice.");
        }

        private DateTimeOffset GetFailureDueAt(int attempt, bool poison)
        {
            if (poison)
            {
                return _clock.UtcNow.Add(Constants.ConsistencyRecoveryPoisonBackoff);
            }

            var exponent = Math.Min(attempt - 1, 6);
            var max = TimeSpan.FromTicks(Math.Min(
                Constants.ConsistencyRecoveryMaxBackoff.Ticks,
                Constants.ConsistencyRecoveryMinBackoff.Ticks * (1L << exponent)));
            return _clock.UtcNow.Add(
                max <= Constants.ConsistencyRecoveryMinBackoff
                    ? Constants.ConsistencyRecoveryMinBackoff
                    : _clock.RandomDelay(Constants.ConsistencyRecoveryMinBackoff, max));
        }

        private async Task<CosmosListDocument> ReadListAsync(
            string listId,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _lists.ReadItemAsync<CosmosListDocument>(
                    listId,
                    new PartitionKey(listId),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task EnsureChannelConvergedAsync(
            string channelId,
            string listId,
            bool shouldContain,
            CancellationToken cancellationToken)
        {
            CosmosChannelDocument channel;
            try
            {
                var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    new PartitionKey(channelId),
                    cancellationToken: cancellationToken);
                channel = response.Resource;
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound && !shouldContain)
            {
                return;
            }

            if (channel == null)
            {
                throw new InvalidOperationException(
                    $"Canonical channel '{channelId}' is missing for an authoritative membership.");
            }

            var contains = channel.SubscribedListIds.Contains(
                listId,
                StringComparer.OrdinalIgnoreCase);
            if (contains != shouldContain
                || channel.SubscriptionCount != channel.SubscribedListIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count())
            {
                throw new InvalidOperationException(
                    $"Canonical channel '{channelId}' did not converge to list truth.");
            }
        }

        private async Task<CosmosRecoveryTicketCursorDocument> ReadTicketAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _recovery.ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                    CosmosRecoveryTicketCursorDocument.DocumentId,
                    new PartitionKey(SystemPartition),
                    cancellationToken: cancellationToken);
                return response.Resource;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        internal static QueryDefinition CreateMembershipDueQuery(
            CosmosRecoveryCursorDocument cursor)
        {
            return new QueryDefinition(
                    "SELECT c.id, c.membershipVersion, c.membershipRecoveryDueAt " +
                    "FROM c WHERE c.membershipRecoveryPending = true " +
                    "AND c.membershipRecoveryDueAt <= @now " +
                    "AND (c.membershipRecoveryDueAt > @afterDueAt OR " +
                    "(c.membershipRecoveryDueAt = @afterDueAt AND c.id > @afterId)) " +
                    "ORDER BY c.membershipRecoveryDueAt ASC, c.id ASC")
                .WithParameter("@now", cursor.CycleNow)
                .WithParameter("@afterDueAt", cursor.AfterDueAt ?? DateTimeOffset.MinValue)
                .WithParameter("@afterId", cursor.AfterId ?? string.Empty);
        }

        internal static QueryDefinition CreateProjectionDueQuery(
            CosmosRecoveryCursorDocument cursor)
        {
            return new QueryDefinition(
                    "SELECT c.id, c.projectionVersion, c.subscriptionGeneration, " +
                    "c.projectionRecoveryDueAt, c.projectionRecoveryStartedAt FROM c " +
                    "WHERE c.projectionRecoveryPending = true " +
                    "AND c.projectionRecoveryDueAt <= @now " +
                    "AND (c.projectionRecoveryDueAt > @afterDueAt OR " +
                    "(c.projectionRecoveryDueAt = @afterDueAt AND c.id > @afterId)) " +
                    "ORDER BY c.projectionRecoveryDueAt ASC, c.id ASC")
                .WithParameter("@now", cursor.CycleNow)
                .WithParameter("@afterDueAt", cursor.AfterDueAt ?? DateTimeOffset.MinValue)
                .WithParameter("@afterId", cursor.AfterId ?? string.Empty);
        }

        internal static QueryDefinition CreateEdgeDueQuery(
            CosmosRecoveryCursorDocument cursor)
        {
            return new QueryDefinition(
                    "SELECT c.id, c.listId, c.generation, c.nextAttemptAt FROM c " +
                    "WHERE c.kind = \"Edge\" AND c.active = true " +
                    "AND c.nextAttemptAt <= @now AND " +
                    "(c.nextAttemptAt > @afterDueAt OR " +
                    "(c.nextAttemptAt = @afterDueAt AND c.listId > @afterListId) OR " +
                    "(c.nextAttemptAt = @afterDueAt AND c.listId = @afterListId AND c.id > @afterId)) " +
                    "ORDER BY c.nextAttemptAt ASC, c.listId ASC, c.id ASC")
                .WithParameter("@now", cursor.CycleNow)
                .WithParameter("@afterDueAt", cursor.AfterDueAt ?? DateTimeOffset.MinValue)
                .WithParameter("@afterListId", cursor.AfterListId ?? string.Empty)
                .WithParameter("@afterId", cursor.AfterId ?? string.Empty);
        }

        internal static QueryDefinition CreateLifecycleDueQuery(
            CosmosRecoveryCursorDocument cursor)
        {
            return new QueryDefinition(
                    "SELECT c.id, c.listId, c.edgeGeneration, c.nextCheckAt FROM c " +
                    "WHERE c.kind = \"Lifecycle\" AND c.nextCheckAt <= @now AND " +
                    "(c.nextCheckAt > @afterDueAt OR " +
                    "(c.nextCheckAt = @afterDueAt AND c.listId > @afterListId) OR " +
                    "(c.nextCheckAt = @afterDueAt AND c.listId = @afterListId AND c.id > @afterId)) " +
                    "ORDER BY c.nextCheckAt ASC, c.listId ASC, c.id ASC")
                .WithParameter("@now", cursor.CycleNow)
                .WithParameter("@afterDueAt", cursor.AfterDueAt ?? DateTimeOffset.MinValue)
                .WithParameter("@afterListId", cursor.AfterListId ?? string.Empty)
                .WithParameter("@afterId", cursor.AfterId ?? string.Empty);
        }

        private static string ToKebabCase(string value)
        {
            return string.Concat(value.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $"-{char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString()));
        }

        private void RecordOptimisticRetry(string workKind, string id)
        {
            ConflictCounter.Add(1);
            RetryCounter.Add(1);
            CosmosRecoveryTelemetry.RecordConflict(
                "RecoveryService",
                workKind,
                retry: true);
            _logger.LogWarning(
                "Cosmos recovery optimistic concurrency conflict will retry. WorkKind={WorkKind}; Id={Id}; Result={Result}.",
                workKind,
                id,
                "Retry");
        }

        private void RecordContentionDeferred(string workKind, string id)
        {
            ConflictCounter.Add(1);
            CosmosRecoveryTelemetry.RecordConflict(
                "RecoveryService",
                workKind,
                retry: false);
            _logger.LogInformation(
                "Cosmos recovery contention exhausted its retry and was deferred. WorkKind={WorkKind}; Id={Id}; Result={Result}.",
                workKind,
                id,
                "Deferred");
        }

        private sealed class MembershipWorkItem
        {
            public string Id { get; set; }
            public long MembershipVersion { get; set; }
            public DateTimeOffset MembershipRecoveryDueAt { get; set; }
        }

        private sealed class ProjectionWorkItem
        {
            public string Id { get; set; }
            public long ProjectionVersion { get; set; }
            public long SubscriptionGeneration { get; set; }
            public DateTimeOffset ProjectionRecoveryDueAt { get; set; }
            public DateTimeOffset? ProjectionRecoveryStartedAt { get; set; }
        }

        private sealed class LifecycleWorkItem
        {
            public string Id { get; set; }
            public string ListId { get; set; }
            public long EdgeGeneration { get; set; }
            public DateTimeOffset NextCheckAt { get; set; }
        }

        private sealed class EdgeWorkItem
        {
            public string Id { get; set; }
            public string ListId { get; set; }
            public long Generation { get; set; }
            public DateTimeOffset NextAttemptAt { get; set; }
        }

        private sealed class PageResult
        {
            public int Examined { get; set; }
            public int Claimed { get; set; }
            public int Succeeded { get; set; }
            public int Failed { get; set; }
            public int Poison { get; set; }
            public double RequestCharge { get; set; }
            public bool HasMore { get; set; }
            public bool FoundEligibleWork { get; set; }
            public int PendingItems { get; set; }
            public DateTimeOffset? OldestPendingAt { get; set; }
        }

        private sealed class RecoveryAdmissionBudget
        {
            private readonly int _maxItems;
            private readonly double _ruBudget;
            private readonly CosmosRequestChargeScope _scope;
            private double _queryChargeFallback;

            internal RecoveryAdmissionBudget(
                int maxItems,
                double ruBudget,
                CosmosRequestChargeScope scope)
            {
                _maxItems = maxItems;
                _ruBudget = ruBudget;
                _scope = scope;
            }

            internal int ProcessedItems { get; private set; }
            internal int RemainingItems => _maxItems - ProcessedItems;

            internal bool CanSchedule =>
                ProcessedItems < _maxItems && MeasuredCharge < _ruBudget;

            internal void RecordQueryCharge(double requestCharge)
            {
                _queryChargeFallback += requestCharge;
            }

            internal bool TryAdmitItem()
            {
                if (!CanSchedule)
                {
                    return false;
                }

                ProcessedItems++;
                return true;
            }

            private double MeasuredCharge =>
                _scope.RequestCharge > 0
                    ? _scope.RequestCharge
                    : _queryChargeFallback;
        }

        private sealed record WorkOutcome(
            bool Claimed,
            bool Succeeded,
            bool HasMore,
            int Examined);

        private sealed record FailureState(
            int Attempt,
            bool Poison,
            DateTimeOffset? NextAttemptAt)
        {
            internal static FailureState None { get; } = new(0, false, null);
        }
    }
}
