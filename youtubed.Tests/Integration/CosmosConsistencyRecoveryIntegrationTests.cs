using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosConsistencyRecoveryIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosConsistencyRecoveryIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task AddAndRemoveMaintainTransactionalRecoveryAndReverseInvariants()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var repository = CreateListRepository(clock);

            await repository.CreateAsync(CreateList(listId, clock));
            var lifecycleBeforeListMutation = await Recovery.ReadItemAsync<CosmosRecoveryLifecycleDocument>(
                CosmosRecoveryLifecycleDocument.DocumentId,
                new PartitionKey(listId.ToString("D")));
            Assert.Equal(0, lifecycleBeforeListMutation.Resource.ActiveEdgeCount);

            await repository.AddChannelAsync(listId, channelId);

            var listAfterAdd = await ReadListAsync(listId);
            var channelAfterAdd = await ReadChannelAsync(channelId);
            var lifecycleAfterAdd = await ReadLifecycleAsync(listId);
            var edgeAfterAdd = await ReadEdgeAsync(listId, channelId);
            Assert.Single(listAfterAdd.Channels);
            Assert.Equal(1, listAfterAdd.MembershipVersion);
            Assert.False(listAfterAdd.MembershipRecoveryPending);
            Assert.Equal(new[] { listId.ToString("D") }, channelAfterAdd.SubscribedListIds);
            Assert.Equal(1, channelAfterAdd.SubscriptionCount);
            Assert.Equal(1, channelAfterAdd.SubscriptionGeneration);
            Assert.Null(channelAfterAdd.OrphanedAfter);
            Assert.Equal(-1, channelAfterAdd.Ttl);
            Assert.Equal(1, lifecycleAfterAdd.ActiveEdgeCount);
            Assert.Equal(1, lifecycleAfterAdd.EdgeGeneration);
            Assert.Equal("Tracked", edgeAfterAdd.State);
            Assert.Null(edgeAfterAdd.NextAttemptAt);

            await repository.RemoveChannelAsync(listId, channelId);

            var listAfterRemove = await ReadListAsync(listId);
            var channelAfterRemove = await ReadChannelAsync(channelId);
            var lifecycleAfterRemove = await ReadLifecycleAsync(listId);
            Assert.Empty(listAfterRemove.Channels);
            Assert.Equal(2, listAfterRemove.MembershipVersion);
            Assert.False(listAfterRemove.MembershipRecoveryPending);
            Assert.Empty(channelAfterRemove.SubscribedListIds);
            Assert.Equal(0, channelAfterRemove.SubscriptionCount);
            Assert.Equal(2, channelAfterRemove.SubscriptionGeneration);
            Assert.NotNull(channelAfterRemove.OrphanedAfter);
            Assert.True(channelAfterRemove.Ttl > 0);
            Assert.Equal(0, lifecycleAfterRemove.ActiveEdgeCount);
            Assert.Equal(2, lifecycleAfterRemove.EdgeGeneration);
            Assert.Null(await TryReadEdgeAsync(listId, channelId));
        }

        [CosmosFact]
        public async Task ExplicitDeletionResumesAfterPartialSeedingAndOrphansUnavailableChannels()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelIds = new[]
            {
                $"UC-{Guid.NewGuid():N}",
                $"UC-{Guid.NewGuid():N}"
            };
            foreach (var channelId in channelIds)
            {
                await SeedChannelAsync(channelId, clock);
                var channel = await ReadChannelAsync(channelId);
                channel.SubscribedListIds = new[] { listId.ToString("D") };
                channel.SubscriptionCount = 1;
                channel.OrphanedAfter = null;
                channel.Ttl = -1;
                if (channelId == channelIds[1])
                {
                    channel.Status = ChannelStatus.Unavailable.ToString();
                    channel.StatusReason = ChannelStatusReason.NotFound.ToString();
                }
                await Channels.ReplaceItemAsync(
                    channel,
                    channel.Id,
                    new PartitionKey(channel.Id),
                    new ItemRequestOptions { IfMatchEtag = channel.ETag });
            }

            var list = CreateListDocument(
                listId,
                clock,
                channelIds[0],
                membershipVersion: 2,
                pending: false);
            list.Channels = list.Channels.Append(new CosmosProjectedChannelDocument
            {
                Id = channelIds[1],
                Status = ChannelStatus.Unavailable.ToString(),
                StatusReason = ChannelStatusReason.NotFound.ToString(),
                Videos = Array.Empty<CosmosVideoDocument>()
            }).ToArray();
            await Lists.CreateItemAsync(list, new PartitionKey(list.Id));

            var seeded = 0;
            var interruptedRepository = CreateListRepository(
                clock,
                new CosmosRecoveryInterleavingHooks
                {
                    AfterLifecycleSideEffectAsync = (_, sideEffect) =>
                    {
                        if (sideEffect.StartsWith("EdgeSeeded:", StringComparison.Ordinal)
                            && Interlocked.Increment(ref seeded) == 1)
                        {
                            throw new InvalidOperationException("injected lifecycle interruption");
                        }
                        return Task.CompletedTask;
                    }
                });
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => interruptedRepository.DeleteAsync(listId));
            Assert.NotNull(await ReadListAsync(listId));
            Assert.Equal("Deleting", (await ReadLifecycleAsync(listId)).State);

            await DeleteRecoverySystemCursorsAsync();
            var result = await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            Assert.True(result.Succeeded > 0);
            await Assert.ThrowsAsync<CosmosException>(() => ReadListAsync(listId));
            Assert.Null(await TryReadLifecycleAsync(listId));
            foreach (var channelId in channelIds)
            {
                var channel = await ReadChannelAsync(channelId);
                Assert.Empty(channel.SubscribedListIds);
                Assert.Equal(0, channel.SubscriptionCount);
                Assert.NotNull(channel.OrphanedAfter);
                Assert.True(channel.Ttl > 0);
                Assert.Null(await TryReadEdgeAsync(listId, channelId));
            }
        }

        [CosmosFact]
        public async Task EveryLifecycleDurableSideEffectIsRestartable()
        {
            foreach (var sideEffectKind in new[]
            {
                "Deleting",
                "FirstEdgeSeeded",
                "SecondEdgeSeeded",
                "ListDeleted",
                "FirstChannelRepaired",
                "SecondChannelRepaired",
                "MissingObserved",
                "FirstEdgeRetired",
                "SecondEdgeRetired",
                "LifecycleCompleted"
            })
            {
                var clock = CreateClock();
                var listId = Guid.NewGuid();
                var channelIds = new[]
                {
                    $"UC-side-effect-a-{Guid.NewGuid():N}",
                    $"UC-side-effect-b-{Guid.NewGuid():N}"
                };
                await SeedLegacyListWithReverseReferencesAsync(
                    listId,
                    channelIds,
                    clock);
                var target = sideEffectKind switch
                {
                    "FirstEdgeSeeded" => $"EdgeSeeded:{channelIds[0]}",
                    "SecondEdgeSeeded" => $"EdgeSeeded:{channelIds[1]}",
                    "FirstChannelRepaired" => $"ChannelRepaired:{channelIds[0]}",
                    "SecondChannelRepaired" => $"ChannelRepaired:{channelIds[1]}",
                    "FirstEdgeRetired" => $"EdgeRetired:{channelIds[0]}",
                    "SecondEdgeRetired" => $"EdgeRetired:{channelIds[1]}",
                    _ => sideEffectKind
                };
                var injected = 0;
                var hooks = new CosmosRecoveryInterleavingHooks
                {
                    AfterLifecycleSideEffectAsync = (_, sideEffect) =>
                    {
                        if (string.Equals(sideEffect, target, StringComparison.Ordinal)
                            && Interlocked.Exchange(ref injected, 1) == 0)
                        {
                            throw new InvalidOperationException(
                                $"injected after {sideEffect}");
                        }
                        return Task.CompletedTask;
                    }
                };

                _ = await CaptureAsync(
                    () => CreateListRepository(clock, hooks).DeleteAsync(listId));
                for (var pass = 0; pass < 8; pass++)
                {
                    var lifecycle = await TryReadLifecycleAsync(listId);
                    if (lifecycle == null)
                    {
                        break;
                    }
                    if (lifecycle.NextCheckAt > clock.UtcNow)
                    {
                        clock.UtcNow = lifecycle.NextCheckAt;
                    }
                    await DeleteRecoverySystemCursorsAsync();
                    _ = await CaptureAsync(
                        () => CreateRecoveryService(clock, hooks).RecoverAsync(
                            ConsistencyRecoveryPassBudget.Default,
                            CancellationToken.None));
                }

                Assert.Equal(1, Volatile.Read(ref injected));
                Assert.Null(await TryReadLifecycleAsync(listId));
                foreach (var channelId in channelIds)
                {
                    var channel = await ReadChannelAsync(channelId);
                    Assert.Empty(channel.SubscribedListIds);
                    Assert.Equal(0, channel.SubscriptionCount);
                    Assert.NotNull(channel.OrphanedAfter);
                    Assert.Null(await TryReadEdgeAsync(listId, channelId));
                }
            }
        }

        [CosmosFact]
        public async Task EarlyLifecycleDeadlineReschedulesFromRenewedListTruth()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var repository = CreateListRepository(clock);
            await repository.CreateAsync(CreateList(listId, clock));

            var list = await ReadListAsync(listId);
            list.ExpiredAfter = clock.UtcNow.AddDays(90);
            list.Ttl = CosmosDocumentMapper.GetTtlSeconds(list.ExpiredAfter, clock.UtcNow);
            await Lists.ReplaceItemAsync(
                list,
                list.Id,
                new PartitionKey(list.Id),
                new ItemRequestOptions { IfMatchEtag = list.ETag });
            var lifecycle = await ReadLifecycleAsync(listId);
            lifecycle.NextCheckAt = clock.UtcNow.AddMinutes(-1);
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(lifecycle.ListId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            lifecycle = await ReadLifecycleAsync(listId);
            Assert.Equal("Active", lifecycle.State);
            Assert.Equal(list.ExpiredAfter, lifecycle.ExpiredAfter);
            Assert.Equal(list.ExpiredAfter, lifecycle.NextCheckAt);
            Assert.NotNull(await ReadListAsync(listId));
        }

        [CosmosFact]
        public async Task LifecycleCleanupCannotEraseMembershipReaddedDuringRepair()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var repository = CreateListRepository(clock);
            await repository.CreateAsync(CreateList(listId, clock));
            await repository.AddChannelAsync(listId, channelId);
            await Lists.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                new PartitionKey(listId.ToString("D")));
            var lifecycle = await ReadLifecycleAsync(listId);
            lifecycle.NextCheckAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(lifecycle.ListId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });

            var readded = 0;
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                BeforeLifecycleEdgeAsync = async _ =>
                {
                    if (Interlocked.Exchange(ref readded, 1) != 0)
                    {
                        return;
                    }
                    await Lists.CreateItemAsync(
                        CreateListDocument(
                            listId,
                            clock,
                            channelId,
                            membershipVersion: 1,
                            pending: false),
                        new PartitionKey(listId.ToString("D")));
                }
            };
            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock, hooks).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            var channel = await ReadChannelAsync(channelId);
            Assert.Contains(listId.ToString("D"), channel.SubscribedListIds);
            Assert.Equal(1, channel.SubscriptionCount);
            Assert.Null(channel.OrphanedAfter);
            Assert.Equal(-1, channel.Ttl);
            Assert.Equal("Active", (await ReadLifecycleAsync(listId)).State);
            Assert.NotNull(await ReadEdgeAsync(listId, channelId));
        }

        [CosmosFact]
        public async Task PhysicalListTtlDeletionRepairsReverseReferenceAndChannelAlsoExpires()
        {
            using var metrics = new RecordingMeterListener("youtubed.cosmos.recovery");
            var clock = CreateClock();
            var options = new CosmosRecoveryOptions
            {
                ChannelOrphanRetention = TimeSpan.FromSeconds(2)
            };
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var repository = CreateListRepository(clock, options: options);
            var expiring = CreateList(listId, clock);
            expiring.ExpiredAfter = clock.UtcNow.AddSeconds(2);
            await repository.CreateAsync(expiring);
            await repository.AddChannelAsync(listId, channelId);

            await WaitForNotFoundAsync(
                Lists,
                listId.ToString("D"),
                TimeSpan.FromSeconds(90));
            clock.UtcNow = expiring.ExpiredAfter.AddSeconds(1);
            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock, options: options).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            var orphan = await ReadChannelAsync(channelId);
            Assert.Empty(orphan.SubscribedListIds);
            Assert.Equal(0, orphan.SubscriptionCount);
            Assert.NotNull(orphan.OrphanedAfter);
            Assert.InRange(orphan.Ttl.Value, 1, 2);
            Assert.Null(await TryReadLifecycleAsync(listId));
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.list_not_found"
                    && measurement.Value >= 1);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.lifecycle.orphan_transitions"
                    && measurement.Value == 1);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.pass.items"
                    && measurement.Value >= 2);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.request_charge"
                    && measurement.Value > 0);
            await WaitForNotFoundAsync(
                Channels,
                channelId,
                TimeSpan.FromSeconds(90));
        }

        [CosmosFact]
        public async Task LifecycleFailuresPreserveFirst404AttemptsBecomePoisonAndLeaseIsTakenOver()
        {
            using var metrics = new RecordingMeterListener("youtubed.cosmos.recovery");
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var lifecycle = await store.CreateLifecycleAsync(
                listId,
                clock.UtcNow,
                CancellationToken.None);
            lifecycle.Owner = "stopped-instance";
            lifecycle.LeaseUntil = clock.UtcNow.AddMinutes(-1);
            lifecycle.NextCheckAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });
            var logger = new RecordingLogger<CosmosConsistencyRecoveryService>();
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                AfterLifecycleSideEffectAsync = (_, sideEffect) =>
                {
                    if (sideEffect == "MissingObserved")
                    {
                        throw new InvalidOperationException("persistent lifecycle failure");
                    }
                    return Task.CompletedTask;
                }
            };
            DateTimeOffset? first404 = null;
            DateTimeOffset? poisonAttemptAt = null;

            for (var attempt = 1;
                attempt <= Constants.ConsistencyRecoveryPoisonAttemptCount;
                attempt++)
            {
                if (attempt == Constants.ConsistencyRecoveryPoisonAttemptCount)
                {
                    poisonAttemptAt = clock.UtcNow;
                }
                await DeleteRecoverySystemCursorsAsync();
                await CreateRecoveryService(clock, hooks, logger).RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    CancellationToken.None);
                lifecycle = await store.ReadLifecycleAsync(listId, CancellationToken.None);
                first404 ??= lifecycle.MissingObservedAt;
                Assert.Equal(first404, lifecycle.MissingObservedAt);
                Assert.Equal(attempt, lifecycle.Attempt);
                Assert.Equal("InvalidOperationException", lifecycle.LastErrorClass);
                var expectedPoisonEvidence =
                    attempt == Constants.ConsistencyRecoveryPoisonAttemptCount
                        ? 1
                        : 0;
                Assert.Equal(
                    expectedPoisonEvidence,
                    logger.Entries.Count(entry =>
                        entry.Level == LogLevel.Error
                        && entry.Message.Contains(
                            "WorkKind=LifecycleDue",
                            StringComparison.Ordinal)
                        && entry.Message.Contains(
                            "Result=Poison",
                            StringComparison.Ordinal)));
                Assert.Equal(
                    expectedPoisonEvidence,
                    metrics.Measurements.Count(measurement =>
                        measurement.Name == "recovery.poison"
                        && measurement.Value == 1));
                clock.UtcNow = lifecycle.NextCheckAt;
            }

            Assert.Equal(
                poisonAttemptAt.Value.Add(Constants.ConsistencyRecoveryPoisonBackoff),
                lifecycle.NextCheckAt);
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("WorkKind=LifecycleDue", StringComparison.Ordinal)
                    && entry.Message.Contains("Attempt=10", StringComparison.Ordinal)
                    && entry.Message.Contains("Result=Poison", StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                entry => entry.Message.Contains("Result=LeaseTakenOver", StringComparison.Ordinal));
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.lease_takeovers"
                    && measurement.Value == 1);
            Assert.Single(
                metrics.Measurements,
                measurement =>
                    measurement.Name == "recovery.poison"
                    && measurement.Value == 1);

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);
            Assert.Null(await store.ReadLifecycleAsync(listId, CancellationToken.None));
        }

        [CosmosFact]
        public async Task First404CleanupAgeEmitsFifteenMinuteOperationalSignal()
        {
            using var metrics = new RecordingMeterListener("youtubed.cosmos.recovery");
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var lifecycle = await store.CreateLifecycleAsync(
                listId,
                clock.UtcNow,
                CancellationToken.None);
            lifecycle.State = "Cleaning";
            lifecycle.MissingObservedAt = clock.UtcNow
                .Subtract(Constants.ConsistencyRecoveryLifecycleCleanupSlo)
                .AddSeconds(-1);
            lifecycle.Attempt = 4;
            lifecycle.LastErrorClass = "PriorFailure";
            lifecycle.NextCheckAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });
            var logger = new RecordingLogger<CosmosConsistencyRecoveryService>();

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock, logger: logger).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.lifecycle.cleanup_age"
                    && measurement.WorkKind == "LifecycleDue"
                    && measurement.Value
                        >= Constants.ConsistencyRecoveryLifecycleCleanupSlo.TotalMilliseconds);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.lifecycle.cleanup_overdue"
                    && measurement.WorkKind == "LifecycleDue"
                    && measurement.Value == 1);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.lifecycle.overdue_age"
                    && measurement.Value >= 0);
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Warning
                    && entry.Message.Contains("first-404 SLO", StringComparison.Ordinal)
                    && entry.Message.Contains(listId, StringComparison.Ordinal));
            Assert.Null(await store.ReadLifecycleAsync(listId, CancellationToken.None));
        }

        [CosmosFact]
        public async Task CleanupIsBoundedRestartsOnExternalGenerationAndIncludesEveryActiveState()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(listId, clock.UtcNow, default);
            var channelIds = new[]
            {
                $"UC-active-candidate-{Guid.NewGuid():N}",
                $"UC-active-leased-{Guid.NewGuid():N}",
                $"UC-active-poison-{Guid.NewGuid():N}"
            };
            var edges = new List<CosmosRecoveryEdgeDocument>();
            foreach (var channelId in channelIds)
            {
                edges.Add(await store.ActivateCandidateAsync(
                    listId,
                    channelId,
                    "mutation-owner",
                    default));
            }
            var leased = edges[1];
            leased.State = "Due";
            leased.Owner = "other-instance";
            leased.LeaseUntil = clock.UtcNow.AddHours(1);
            await Recovery.ReplaceItemAsync(
                leased,
                leased.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = leased.ETag });
            var poison = edges[2];
            poison.State = "Poison";
            poison.Owner = null;
            poison.LeaseUntil = null;
            poison.Attempt = Constants.ConsistencyRecoveryPoisonAttemptCount;
            poison.NextAttemptAt = clock.UtcNow.AddDays(1);
            await Recovery.ReplaceItemAsync(
                poison,
                poison.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = poison.ETag });

            var inserted = 0;
            var insertedChannelId = $"UC-active-new-{Guid.NewGuid():N}";
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                BeforeLifecycleEdgeAsync = async _ =>
                {
                    if (Interlocked.Exchange(ref inserted, 1) == 0)
                    {
                        await store.ActivateCandidateAsync(
                            listId,
                            insertedChannelId,
                            "external-instance",
                            default);
                    }
                }
            };
            await DeleteRecoverySystemCursorsAsync();
            var first = await CreateRecoveryService(clock, hooks).RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 2, 2_000),
                default);
            Assert.True(first.HasMoreEligibleWork);
            var afterRestart = await store.ReadLifecycleAsync(listId, default);
            Assert.Null(afterRestart.CleanupEdgeAfterChannelId);
            Assert.Equal(afterRestart.EdgeGeneration, afterRestart.CleanupTraversalEdgeGeneration);

            for (var pass = 0; pass < 12; pass++)
            {
                await DeleteRecoverySystemCursorsAsync();
                await CreateRecoveryService(clock).RecoverAsync(
                    new ConsistencyRecoveryPassBudget(1, 2, 2_000),
                    default);
                if (await store.ReadLifecycleAsync(listId, default) == null)
                {
                    break;
                }
            }

            Assert.Null(await store.ReadLifecycleAsync(listId, default));
            foreach (var channelId in channelIds.Append(insertedChannelId))
            {
                Assert.Null(await store.ReadEdgeAsync(
                    listId,
                    CosmosRecoveryStore.GetEdgeId(channelId),
                    default));
            }
        }

        [CosmosFact]
        public async Task MoreThan125DistinctFailedAddsRetireTransactionallyWithoutPartitionGrowth()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            await CreateListRepository(clock).CreateAsync(CreateList(listId, clock));
            var listIdText = listId.ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            const int distinctFailedAdds = 130;
            const int failedAddsPerCycle = 25;

            for (var cycleStart = 0;
                cycleStart < distinctFailedAdds;
                cycleStart += failedAddsPerCycle)
            {
                var cycleEnd = Math.Min(
                    distinctFailedAdds,
                    cycleStart + failedAddsPerCycle);
                for (var index = cycleStart; index < cycleEnd; index++)
                {
                    var edge = await store.ActivateCandidateAsync(
                        listIdText,
                        $"UC-failed-add-{index:D3}-{Guid.NewGuid():N}",
                        $"failed-request-{index:D3}",
                        default);
                    await store.MarkDueAsync(edge, default);
                }

                var lifecycle = await store.ReadLifecycleAsync(listIdText, default);
                Assert.InRange(
                    lifecycle.ActiveEdgeCount,
                    1,
                    Constants.RecoveryMaxActiveEdgesPerList);
                Assert.InRange(
                    await CountRecoveryEdgesAsync(listIdText, activeOnly: false),
                    1,
                    Constants.RecoveryMaxActiveEdgesPerList);

                for (var pass = 0; pass < 4; pass++)
                {
                    await DeleteRecoverySystemCursorsAsync();
                    await CreateRecoveryService(clock).RecoverAsync(
                        ConsistencyRecoveryPassBudget.Default,
                        default);
                    if (await CountRecoveryEdgesAsync(
                        listIdText,
                        activeOnly: false) == 0)
                    {
                        break;
                    }
                }

                lifecycle = await store.ReadLifecycleAsync(listIdText, default);
                Assert.Equal(0, lifecycle.ActiveEdgeCount);
                Assert.Equal(0, await CountRecoveryEdgesAsync(
                    listIdText,
                    activeOnly: true));
                Assert.Equal(0, await CountRecoveryEdgesAsync(
                    listIdText,
                    activeOnly: false));
            }
        }

        [CosmosFact]
        public async Task CleanupInstanceCannotEraseConcurrentRepositoryInstanceReadd()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-genuine-readd-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var initialRepository = CreateListRepository(clock);
            await initialRepository.CreateAsync(CreateList(listId, clock));
            await initialRepository.AddChannelAsync(listId, channelId);
            await Lists.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                new PartitionKey(listId.ToString("D")));
            var lifecycle = await ReadLifecycleAsync(listId);
            lifecycle.NextCheckAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(lifecycle.ListId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });

            var cleanupReachedStaleRepair =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCleanup =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                BeforeLifecycleEdgeAsync = async edge =>
                {
                    if (edge.ChannelId == channelId)
                    {
                        cleanupReachedStaleRepair.TrySetResult();
                        await allowCleanup.Task;
                    }
                }
            };
            await DeleteRecoverySystemCursorsAsync();
            var cleanupInstance = CreateRecoveryService(clock, hooks);
            var cleanup = cleanupInstance.RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                default);
            await cleanupReachedStaleRepair.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var repositoryInstance = CreateListRepository(clock);
            await repositoryInstance.CreateAsync(CreateList(listId, clock));
            await repositoryInstance.AddChannelAsync(listId, channelId);
            allowCleanup.SetResult();
            await cleanup;

            var list = await ReadListAsync(listId);
            var channel = await ReadChannelAsync(channelId);
            lifecycle = await ReadLifecycleAsync(listId);
            var edge = await ReadEdgeAsync(listId, channelId);
            Assert.Contains(list.Channels, projected => projected.Id == channelId);
            Assert.Contains(listId.ToString("D"), channel.SubscribedListIds);
            Assert.Equal(1, channel.SubscriptionCount);
            Assert.Null(channel.OrphanedAfter);
            Assert.Equal(-1, channel.Ttl);
            Assert.Equal("Active", lifecycle.State);
            Assert.Equal(1, lifecycle.ActiveEdgeCount);
            Assert.Equal("Tracked", edge.State);
            Assert.Null(edge.Owner);
            Assert.Null(edge.LeaseUntil);
            Assert.False(list.MembershipRecoveryPending);
        }

        [CosmosFact]
        public async Task CounterDriftBlocksCompletionEmitsPoisonEvidenceAndRecountsPartition()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var lifecycle = await store.CreateLifecycleAsync(listId, clock.UtcNow, default);
            lifecycle.ActiveEdgeCount = 1;
            lifecycle.NextCheckAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                lifecycle,
                lifecycle.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = lifecycle.ETag });
            var logger = new RecordingLogger<CosmosConsistencyRecoveryService>();

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock, logger: logger).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                default);
            lifecycle = await store.ReadLifecycleAsync(listId, default);
            Assert.Equal(0, lifecycle.ActiveEdgeCount);
            Assert.Equal(1, lifecycle.EdgeGeneration);
            Assert.Equal(Constants.ConsistencyRecoveryPoisonAttemptCount, lifecycle.Attempt);
            Assert.Equal("ActiveEdgeCountDrift", lifecycle.LastErrorClass);
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains("Result=Recounted", StringComparison.Ordinal));

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                default);
            Assert.Null(await store.ReadLifecycleAsync(listId, default));
        }

        [CosmosFact]
        public async Task FirstMutationBootstrapsLegacyLifecycleAndAllExistingMembershipEdges()
        {
            var clock = CreateClock();
            foreach (var partialBootstrap in new[] { false, true })
            {
                var listId = Guid.NewGuid();
                var existingChannelId =
                    $"UC-legacy-existing-{partialBootstrap}-{Guid.NewGuid():N}";
                var addedChannelId =
                    $"UC-legacy-added-{partialBootstrap}-{Guid.NewGuid():N}";
                await SeedChannelAsync(existingChannelId, clock);
                await SeedChannelAsync(addedChannelId, clock);
                var legacyList = CreateListDocument(
                    listId,
                    clock,
                    existingChannelId,
                    membershipVersion: 7,
                    pending: false);
                await Lists.CreateItemAsync(
                    legacyList,
                    new PartitionKey(legacyList.Id));
                var existingChannel = await ReadChannelAsync(existingChannelId);
                existingChannel.SubscribedListIds = new[] { listId.ToString("D") };
                existingChannel.SubscriptionCount = 1;
                existingChannel.SubscriptionGeneration = 1;
                existingChannel.OrphanedAfter = null;
                existingChannel.Ttl = -1;
                await Channels.ReplaceItemAsync(
                    existingChannel,
                    existingChannel.Id,
                    new PartitionKey(existingChannel.Id),
                    new ItemRequestOptions { IfMatchEtag = existingChannel.ETag });

                if (partialBootstrap)
                {
                    await new CosmosRecoveryStore(
                        Recovery,
                        clock,
                        new CosmosRecoveryOptions()).CreateLifecycleAsync(
                            listId.ToString("D"),
                            legacyList.ExpiredAfter,
                            default);
                }

                await CreateListRepository(clock).AddChannelAsync(
                    listId,
                    addedChannelId);

                var lifecycle = await ReadLifecycleAsync(listId);
                Assert.Equal(2, lifecycle.ActiveEdgeCount);
                Assert.Equal(2, lifecycle.EdgeGeneration);
                Assert.Equal(
                    "Tracked",
                    (await ReadEdgeAsync(listId, existingChannelId)).State);
                Assert.Equal(
                    "Tracked",
                    (await ReadEdgeAsync(listId, addedChannelId)).State);
                Assert.Contains(
                    listId.ToString("D"),
                    (await ReadChannelAsync(existingChannelId)).SubscribedListIds);
                Assert.Contains(
                    listId.ToString("D"),
                    (await ReadChannelAsync(addedChannelId)).SubscribedListIds);
                var updatedList = await ReadListAsync(listId);
                Assert.Equal(2, updatedList.Channels.Count);
                Assert.False(updatedList.MembershipRecoveryPending);
            }
        }

        [CosmosFact]
        public async Task RepairingOneMembershipPreservesEveryUnrelatedReverseMembership()
        {
            var clock = CreateClock();
            var repairedListId = Guid.NewGuid();
            var unrelatedListId = Guid.NewGuid();
            var channelId = $"UC-unrelated-{Guid.NewGuid():N}";
            var channel = CreateChannelDocument(channelId, clock);
            channel.SubscribedListIds = new[]
            {
                repairedListId.ToString("D"),
                unrelatedListId.ToString("D")
            };
            channel.SubscriptionCount = 2;
            channel.SubscriptionGeneration = 1;
            channel.OrphanedAfter = null;
            channel.Ttl = -1;
            await Channels.CreateItemAsync(channel, new PartitionKey(channelId));
            var list = CreateListDocument(
                repairedListId,
                clock,
                channelId,
                membershipVersion: 1,
                pending: true);
            await Lists.CreateItemAsync(list, new PartitionKey(list.Id));
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                list.Id,
                list.ExpiredAfter,
                default);
            var edge = await store.ActivateCandidateAsync(
                list.Id,
                channelId,
                "unrelated-owner",
                default);
            await store.MarkDueAsync(edge, default);

            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                default);

            var repaired = await ReadChannelAsync(channelId);
            Assert.Equal(
                new[]
                {
                    repairedListId.ToString("D"),
                    unrelatedListId.ToString("D")
                }.OrderBy(value => value, StringComparer.Ordinal),
                repaired.SubscribedListIds.OrderBy(
                    value => value,
                    StringComparer.Ordinal));
            Assert.Equal(2, repaired.SubscriptionCount);
        }

        [CosmosFact]
        public async Task FreshProcessRepairsCrashAfterListCommitAndIsIdempotent()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId.ToString("D"),
                clock.UtcNow.AddDays(45),
                CancellationToken.None);
            await store.ActivateCandidateAsync(
                listId.ToString("D"),
                channelId,
                "terminated-request",
                CancellationToken.None);
            await Lists.CreateItemAsync(
                CreateListDocument(
                    listId,
                    clock,
                    channelId,
                    membershipVersion: 1,
                    pending: true),
                new PartitionKey(listId.ToString("D")));
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);

            var freshProcess = CreateRecoveryService(clock);
            var first = await freshProcess.RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);
            var second = await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            var list = await ReadListAsync(listId);
            var channel = await ReadChannelAsync(channelId);
            var lifecycle = await ReadLifecycleAsync(listId);
            Assert.True(first.Succeeded > 0);
            Assert.False(list.MembershipRecoveryPending);
            Assert.Equal(new[] { listId.ToString("D") }, channel.SubscribedListIds);
            Assert.Equal(1, channel.SubscriptionCount);
            Assert.Equal(1, lifecycle.ActiveEdgeCount);
            Assert.Equal("Tracked", (await ReadEdgeAsync(listId, channelId)).State);
            Assert.Equal(0, second.Failed);
        }

        [CosmosFact]
        public async Task FreshProcessCompensatesCrashAfterProvisionalChannelReservation()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId.ToString("D"),
                clock.UtcNow.AddDays(45),
                CancellationToken.None);
            await store.ActivateCandidateAsync(
                listId.ToString("D"),
                channelId,
                "terminated-request",
                CancellationToken.None);
            var channelRepository = new CosmosChannelRepository(
                Channels,
                Lists,
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await channelRepository.ReserveSubscriptionAsync(
                channelId,
                listId,
                CancellationToken.None);
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);

            for (var pass = 0; pass < 8; pass++)
            {
                await CreateRecoveryService(clock).RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    CancellationToken.None);
                if (await TryReadEdgeAsync(listId, channelId) == null)
                {
                    break;
                }
            }

            var channel = await ReadChannelAsync(channelId);
            var lifecycle = await ReadLifecycleAsync(listId);
            Assert.Empty(channel.SubscribedListIds);
            Assert.Equal(0, channel.SubscriptionCount);
            Assert.NotNull(channel.OrphanedAfter);
            Assert.Equal(0, lifecycle.ActiveEdgeCount);
            Assert.Null(await TryReadEdgeAsync(listId, channelId));
        }

        [CosmosFact]
        public async Task ProjectionGenerationChangeRestartsKeysetAndStillProcessesLaterList()
        {
            var clock = CreateClock();
            var listA = Guid.NewGuid();
            var listB = Guid.NewGuid();
            if (listA.CompareTo(listB) > 0)
            {
                (listA, listB) = (listB, listA);
            }

            var channelId = $"UC-{Guid.NewGuid():N}";
            var canonical = CreateChannelDocument(channelId, clock);
            canonical.Title = "current-title";
            canonical.Status = ChannelStatus.Unavailable.ToString();
            canonical.StatusReason = ChannelStatusReason.NotFound.ToString();
            canonical.SubscribedListIds =
                new[] { listA.ToString("D"), listB.ToString("D") };
            canonical.SubscriptionCount = 2;
            canonical.SubscriptionGeneration = 1;
            canonical.ProjectionVersion = 1;
            canonical.ProjectionRecoveryPending = true;
            canonical.ProjectionRecoveryDueAt = clock.UtcNow;
            await Channels.CreateItemAsync(canonical, new PartitionKey(channelId));
            await Lists.CreateItemAsync(
                CreateListDocument(listA, clock, channelId, 0, false, "old-a"),
                new PartitionKey(listA.ToString("D")));
            await Lists.CreateItemAsync(
                CreateListDocument(listB, clock, channelId, 0, false, "old-b"),
                new PartitionKey(listB.ToString("D")));
            var listBBeforeProjection = await ReadListAsync(listB);
            listBBeforeProjection.MembershipVersion = 7;
            listBBeforeProjection.MembershipRecoveryPending = true;
            listBBeforeProjection.MembershipRecoveryDueAt = clock.UtcNow.AddDays(1);
            await Lists.ReplaceItemAsync(
                listBBeforeProjection,
                listBBeforeProjection.Id,
                new PartitionKey(listBBeforeProjection.Id),
                new ItemRequestOptions { IfMatchEtag = listBBeforeProjection.ETag });
            var aWritten = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCheckpoint = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                AfterProjectionListWriteAsync = async writtenListId =>
                {
                    if (writtenListId == listA.ToString("D"))
                    {
                        aWritten.TrySetResult();
                        await allowCheckpoint.Task;
                    }
                }
            };
            var firstPass = CreateProjectionRepository(clock, hooks)
                .RecoverPendingProjectionAsync(
                    channelId,
                    25,
                    CancellationToken.None);
            await aWritten.Task;
            var afterA = await ReadChannelAsync(channelId);
            afterA.SubscribedListIds = new[] { listB.ToString("D") };
            afterA.SubscriptionCount = 1;
            afterA.SubscriptionGeneration++;
            afterA.ProjectionRecoveryPending = true;
            afterA.ProjectionRecoveryDueAt = clock.UtcNow;
            await Channels.ReplaceItemAsync(
                afterA,
                channelId,
                new PartitionKey(channelId),
                new ItemRequestOptions { IfMatchEtag = afterA.ETag });
            allowCheckpoint.SetResult();
            var first = await firstPass;

            var second = await CreateProjectionRepository(clock)
                .RecoverPendingProjectionAsync(channelId, 1, CancellationToken.None);

            Assert.True(first.HasMore);
            Assert.False(second.HasMore);
            Assert.Equal(
                "current-title",
                Assert.Single((await ReadListAsync(listB)).Channels).Title);
            Assert.Equal(7, (await ReadListAsync(listB)).MembershipVersion);
            Assert.True((await ReadListAsync(listB)).MembershipRecoveryPending);
            var completed = await ReadChannelAsync(channelId);
            Assert.False(completed.ProjectionRecoveryPending);
            Assert.Null(completed.ProjectionRecoveryAfterListId);
        }

        [CosmosFact]
        public async Task TicketRotationSurvivesProcessRestartWhenEachPageExhaustsRu()
        {
            await DeleteRecoverySystemCursorsAsync();
            var clock = CreateClock();
            var observed = new[]
            {
                "Projection",
                "EdgeDue",
                "LifecycleDue",
                "Membership"
            };
            foreach (var expectedNext in observed)
            {
                await CreateRecoveryService(clock).RecoverAsync(
                    new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                    CancellationToken.None);
                var cursor = await Recovery.ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                    CosmosRecoveryTicketCursorDocument.DocumentId,
                    new PartitionKey("__system"));
                Assert.Equal(expectedNext, cursor.Resource.NextStartingKind);
            }
        }

        [CosmosFact]
        public async Task ContinuousMembershipCannotStarveProjectionPoisonEdgeOrLifecycleAcrossRestart()
        {
            await DeleteRecoverySystemCursorsAsync();
            var clock = CreateClock();
            var membershipListId = Guid.NewGuid();
            var membershipChannelId = $"UC-fair-member-{Guid.NewGuid():N}";
            await SeedChannelAsync(membershipChannelId, clock);
            var membershipList = CreateListDocument(
                membershipListId,
                clock,
                membershipChannelId,
                1,
                pending: true);
            membershipList.MembershipRecoveryDueAt = clock.UtcNow.AddDays(-100);
            await Lists.CreateItemAsync(
                membershipList,
                new PartitionKey(membershipList.Id));

            var projectionChannelId = $"UC-fair-projection-{Guid.NewGuid():N}";
            var projectionChannel = CreateChannelDocument(projectionChannelId, clock);
            projectionChannel.ProjectionVersion = 1;
            projectionChannel.ProjectionRecoveryPending = true;
            projectionChannel.ProjectionRecoveryDueAt = clock.UtcNow.AddDays(-100);
            await Channels.CreateItemAsync(
                projectionChannel,
                new PartitionKey(projectionChannel.Id));

            var poisonListId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var poisonLifecycle = await store.CreateLifecycleAsync(
                poisonListId,
                clock.UtcNow.AddDays(-100),
                CancellationToken.None);
            var poisonEdge = await store.ActivateCandidateAsync(
                poisonListId,
                $"UC-fair-poison-{Guid.NewGuid():N}",
                "old-owner",
                CancellationToken.None);
            poisonEdge.State = "Poison";
            poisonEdge.Attempt = Constants.ConsistencyRecoveryPoisonAttemptCount;
            poisonEdge.Owner = null;
            poisonEdge.LeaseUntil = null;
            poisonEdge.NextAttemptAt = clock.UtcNow.AddDays(-100);
            await Recovery.ReplaceItemAsync(
                poisonEdge,
                poisonEdge.Id,
                new PartitionKey(poisonEdge.ListId),
                new ItemRequestOptions { IfMatchEtag = poisonEdge.ETag });

            var passResults = new System.Collections.Generic.List<ConsistencyRecoveryPassResult>();
            for (var admittedPage = 0; admittedPage < 4; admittedPage++)
            {
                passResults.Add(await CreateRecoveryService(clock).RecoverAsync(
                    new ConsistencyRecoveryPassBudget(1, 1, 50),
                    CancellationToken.None));
                if (admittedPage < 3)
                {
                    await ReplenishMembershipAsync(membershipListId, clock);
                }
            }

            Assert.False(
                (await ReadChannelAsync(projectionChannelId)).ProjectionRecoveryPending,
                string.Join(
                    ";",
                    passResults.Select(result =>
                        $"ru={result.RequestCharge},items={result.Examined}")));
            Assert.Null(await store.ReadEdgeAsync(
                poisonListId,
                poisonEdge.Id,
                CancellationToken.None));
            var lifecycleCursor =
                (await Recovery.ReadItemAsync<CosmosRecoveryCursorDocument>(
                    "cursor:lifecycle-due",
                    new PartitionKey("__system"))).Resource;
            Assert.NotNull(lifecycleCursor.AfterListId);
            var ticket = (await Recovery.ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                CosmosRecoveryTicketCursorDocument.DocumentId,
                new PartitionKey("__system"))).Resource;
            Assert.Equal("Membership", ticket.NextStartingKind);
            Assert.All(passResults, result => Assert.InRange(result.Examined, 0, 1));
        }

        [CosmosFact]
        public async Task QueryRuExhaustionAdmitsNoItemAndDoesNotAdvancePastIt()
        {
            await DeleteRecoverySystemCursorsAsync();
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var pendingList = CreateListDocument(
                listId,
                clock,
                channelId: null,
                membershipVersion: 1,
                pending: true);
            pendingList.Channels = Array.Empty<CosmosProjectedChannelDocument>();
            await Lists.CreateItemAsync(
                pendingList,
                new PartitionKey(listId.ToString("D")));

            await ForceNextTicketAsync(Recovery, "Membership", clock);
            var exhausted = await CreateRecoveryService(clock).RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                default);

            Assert.Equal(0, exhausted.Examined);
            Assert.True(exhausted.RequestCharge > 0.01);
            Assert.True((await ReadListAsync(listId)).MembershipRecoveryPending);
            var cursor = await ReadCursorAsync(Recovery, "cursor:membership");
            Assert.Null(cursor.AfterId);

            await ForceNextTicketAsync(Recovery, "Membership", clock);
            var resumed = await CreateRecoveryService(clock).RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                default);

            Assert.Equal(1, resumed.Examined);
            Assert.False((await ReadListAsync(listId)).MembershipRecoveryPending);
        }

        [CosmosFact]
        public async Task ConcurrentDuplicateAddConvergesToOneMembershipAndOneEdge()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var first = CreateListRepository(clock);
            var second = CreateListRepository(clock);
            await first.CreateAsync(CreateList(listId, clock));

            var attempts = await Task.WhenAll(
                CaptureAsync(() => first.AddChannelAsync(listId, channelId)),
                CaptureAsync(() => second.AddChannelAsync(listId, channelId)));
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);
            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);

            Assert.Contains(attempts, exception => exception == null);
            Assert.Single((await ReadListAsync(listId)).Channels);
            Assert.Equal(
                new[] { listId.ToString("D") },
                (await ReadChannelAsync(channelId)).SubscribedListIds);
            Assert.Equal(1, (await ReadLifecycleAsync(listId)).ActiveEdgeCount);
            Assert.NotNull(await ReadEdgeAsync(listId, channelId));
        }

        [CosmosFact]
        public async Task StaleRetirementCannotDeleteAReactivatedAddGeneration()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-retire-add-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var initialRepository = CreateListRepository(clock);
            await initialRepository.CreateAsync(CreateList(listId, clock));
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var oldEdge = await store.ActivateCandidateAsync(
                listId.ToString("D"),
                channelId,
                "old-recovery",
                CancellationToken.None);
            oldEdge = await store.MarkDueAsync(oldEdge, CancellationToken.None);
            var staleEdge = await store.ReadEdgeAsync(
                listId.ToString("D"),
                oldEdge.Id,
                CancellationToken.None);
            var staleLifecycle = await ReadLifecycleAsync(listId);
            var reservationReached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowCommit = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                AfterMutationReservationAsync = async _ =>
                {
                    reservationReached.SetResult();
                    await allowCommit.Task;
                }
            };
            var addingRepository = CreateListRepository(clock, hooks);

            var add = addingRepository.AddChannelAsync(listId, channelId);
            await reservationReached.Task;
            var retirement = await store.RetireEdgeAsync(
                staleEdge,
                staleLifecycle,
                0,
                null,
                null,
                CancellationToken.None,
                revalidateAuthoritativeAbsenceAsync: _ => Task.FromResult(true));
            allowCommit.SetResult();
            await add;

            Assert.False(retirement.Retired);
            var list = await ReadListAsync(listId);
            var edge = await ReadEdgeAsync(listId, channelId);
            Assert.Single(list.Channels);
            Assert.True(edge.Active);
            Assert.Equal("Tracked", edge.State);
            Assert.True(edge.Generation > staleEdge.Generation);
            Assert.Equal(1, (await ReadLifecycleAsync(listId)).ActiveEdgeCount);
        }

        [CosmosFact]
        public async Task RetirementRetriesOneIncidentalEtagConflictAfterExactTruthRevalidation()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var channelId = $"UC-retire-retry-{Guid.NewGuid():N}";
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId,
                clock.UtcNow.AddDays(45),
                default);
            var edge = await store.ActivateCandidateAsync(
                listId,
                channelId,
                "exact-owner",
                default);
            var staleLifecycle = await store.ReadLifecycleAsync(listId, default);
            var currentLifecycle = await store.ReadLifecycleAsync(listId, default);
            currentLifecycle.NextAttemptAt = clock.UtcNow.AddMinutes(1);
            await Recovery.ReplaceItemAsync(
                currentLifecycle,
                currentLifecycle.Id,
                new PartitionKey(listId),
                new ItemRequestOptions { IfMatchEtag = currentLifecycle.ETag });

            var revalidations = 0;
            var result = await store.RetireEdgeAsync(
                edge,
                staleLifecycle,
                membershipVersion: 0,
                afterChannelId: null,
                afterId: null,
                cancellationToken: default,
                revalidateAuthoritativeAbsenceAsync: _ =>
                {
                    revalidations++;
                    return Task.FromResult(true);
                });

            Assert.True(result.Retired);
            Assert.Equal(1, revalidations);
            Assert.Null(await store.ReadEdgeAsync(listId, edge.Id, default));
            Assert.Equal(
                0,
                (await store.ReadLifecycleAsync(listId, default)).ActiveEdgeCount);
        }

        [CosmosFact]
        public async Task CandidateActivationCannotStealForeignLeaseAndCommitRejectsShortLease()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-lease-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var repository = CreateListRepository(clock);
            await repository.CreateAsync(CreateList(listId, clock));
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            var owned = await store.ActivateCandidateAsync(
                listId.ToString("D"),
                channelId,
                "first-owner",
                CancellationToken.None);

            await Assert.ThrowsAsync<RecoveryLeaseUnavailableException>(
                () => store.ActivateCandidateAsync(
                    listId.ToString("D"),
                    channelId,
                    "second-owner",
                    CancellationToken.None));
            var afterRejectedSteal = await ReadEdgeAsync(listId, channelId);
            Assert.Equal(owned.Generation, afterRejectedSteal.Generation);
            Assert.Equal("first-owner", afterRejectedSteal.Owner);

            clock.UtcNow = owned.LeaseUntil.Value.AddSeconds(1);
            var options = new CosmosRecoveryOptions();
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                AfterMutationReservationAsync = _ =>
                {
                    clock.UtcNow = clock.UtcNow
                        .Add(options.LeaseDuration)
                        .Subtract(options.MutationCommitSafetyWindow)
                        .AddSeconds(1);
                    return Task.CompletedTask;
                }
            };
            var shortLeaseRepository = CreateListRepository(clock, hooks, options);
            await Assert.ThrowsAsync<RecoveryLeaseUnavailableException>(
                () => shortLeaseRepository.AddChannelAsync(listId, channelId));
            Assert.Empty((await ReadListAsync(listId)).Channels);
            Assert.NotNull(await ReadEdgeAsync(listId, channelId));
        }

        [CosmosFact]
        public async Task ConcurrentAddRemoveRaceConvergesReverseReferenceToCurrentListTruth()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var first = CreateListRepository(clock);
            var second = CreateListRepository(clock);
            await first.CreateAsync(CreateList(listId, clock));

            await Task.WhenAll(
                CaptureAsync(() => first.AddChannelAsync(listId, channelId)),
                CaptureAsync(() => second.RemoveChannelAsync(listId, channelId)));
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);
            for (var pass = 0; pass < 8; pass++)
            {
                await CreateRecoveryService(clock).RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    CancellationToken.None);
            }

            var listContains = (await ReadListAsync(listId)).Channels.Any(
                channel => channel.Id == channelId);
            var channel = await ReadChannelAsync(channelId);
            Assert.Equal(
                listContains,
                channel.SubscribedListIds.Contains(
                    listId.ToString("D"),
                    StringComparer.OrdinalIgnoreCase));
            Assert.Equal(
                channel.SubscribedListIds
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                channel.SubscriptionCount);
        }

        [CosmosFact]
        public async Task MembershipRetirementAdoptsOwnGenerationButExternalRetirementRestarts()
        {
            await DeleteRecoverySystemCursorsAsync();
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var firstChannelId = $"UC-a-{Guid.NewGuid():N}";
            var secondChannelId = $"UC-b-{Guid.NewGuid():N}";
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId.ToString("D"),
                clock.UtcNow.AddDays(45),
                CancellationToken.None);
            await store.ActivateCandidateAsync(
                listId.ToString("D"),
                firstChannelId,
                "terminated-1",
                CancellationToken.None);
            await store.ActivateCandidateAsync(
                listId.ToString("D"),
                secondChannelId,
                "terminated-2",
                CancellationToken.None);
            var emptyList = CreateListDocument(
                listId,
                clock,
                firstChannelId,
                membershipVersion: 1,
                pending: true);
            emptyList.Channels = Array.Empty<CosmosProjectedChannelDocument>();
            emptyList.MembershipRecoveryDueAt = clock.UtcNow.AddDays(-1000);
            await Lists.CreateItemAsync(
                emptyList,
                new PartitionKey(listId.ToString("D")));
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);

            await CreateRecoveryService(clock).RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                CancellationToken.None);

            var afterOwnRetirement = await ReadLifecycleAsync(listId);
            Assert.Equal(1, afterOwnRetirement.ActiveEdgeCount);
            Assert.Equal(3, afterOwnRetirement.EdgeGeneration);
            Assert.Equal(3, afterOwnRetirement.MembershipTraversalEdgeGeneration);
            Assert.Equal(firstChannelId, afterOwnRetirement.MembershipEdgeAfterChannelId);

            var externalEdgeId = CosmosRecoveryStore.GetEdgeId(secondChannelId);
            await DeleteRecoverySystemCursorsAsync();
            var recoveryReachedEdge = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var allowRecovery = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                BeforeMembershipEdgeAsync = async edge =>
                {
                    if (edge.Id == externalEdgeId)
                    {
                        recoveryReachedEdge.TrySetResult();
                        await allowRecovery.Task;
                    }
                }
            };
            var racingRecovery = CreateRecoveryService(clock, hooks).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);
            await recoveryReachedEdge.Task;
            var externalEdge = await ReadEdgeAsync(listId, secondChannelId);
            var externalLifecycle = await ReadLifecycleAsync(listId);
            var external = await store.RetireEdgeAsync(
                externalEdge,
                externalLifecycle,
                1,
                externalLifecycle.MembershipEdgeAfterChannelId,
                externalLifecycle.MembershipEdgeAfterId,
                CancellationToken.None);
            allowRecovery.SetResult();
            await racingRecovery;
            Assert.True(external.Retired);
            Assert.Equal(4, external.EdgeGeneration);

            for (var pass = 0; pass < 12; pass++)
            {
                await CreateRecoveryService(clock).RecoverAsync(
                    new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                    CancellationToken.None);
                if (!(await ReadListAsync(listId)).MembershipRecoveryPending)
                {
                    break;
                }
            }

            var completedList = await ReadListAsync(listId);
            var completedLifecycle = await ReadLifecycleAsync(listId);
            Assert.False(completedList.MembershipRecoveryPending);
            Assert.Null(completedLifecycle.MembershipEdgeAfterChannelId);
            Assert.Equal(4, completedLifecycle.MembershipTraversalEdgeGeneration);
        }

        [CosmosFact]
        public async Task RecoveryCapacityRejectsThe126thDistinctActiveEdge()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId,
                clock.UtcNow.AddDays(45),
                CancellationToken.None);
            for (var index = 0; index < Constants.RecoveryMaxActiveEdgesPerList; index++)
            {
                await store.ActivateCandidateAsync(
                    listId,
                    $"UC-cap-{index:D3}",
                    "capacity-test",
                    CancellationToken.None);
            }

            await Assert.ThrowsAsync<RecoveryCapacityExceededException>(
                () => store.ActivateCandidateAsync(
                    listId,
                    "UC-cap-overflow",
                    "capacity-test",
                    CancellationToken.None));
            var lifecycle = (await Recovery.ReadItemAsync<CosmosRecoveryLifecycleDocument>(
                CosmosRecoveryLifecycleDocument.DocumentId,
                new PartitionKey(listId))).Resource;
            Assert.Equal(Constants.RecoveryMaxActiveEdgesPerList, lifecycle.ActiveEdgeCount);
            Assert.Equal(Constants.RecoveryMaxActiveEdgesPerList, lifecycle.EdgeGeneration);

            var retiredEdge = await store.ReadEdgeAsync(
                listId,
                CosmosRecoveryStore.GetEdgeId("UC-cap-000"),
                CancellationToken.None);
            var retirement = await store.RetireEdgeAsync(
                retiredEdge,
                lifecycle,
                0,
                null,
                null,
                CancellationToken.None);
            Assert.True(retirement.Retired);
            await store.ActivateCandidateAsync(
                listId,
                "UC-cap-replacement",
                "capacity-test",
                CancellationToken.None);
            var edgeCountQuery = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.kind = \"Edge\"");
            using var edgeCountIterator = Recovery.GetItemQueryIterator<int>(
                edgeCountQuery,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(listId),
                    MaxItemCount = 1
                });
            Assert.Equal(
                Constants.RecoveryMaxActiveEdgesPerList,
                (await edgeCountIterator.ReadNextAsync()).Single());
        }

        [CosmosFact]
        public async Task RecoveryDocumentAndChannelPreflightCeilingsRejectBeforeWrite()
        {
            var clock = CreateClock();
            var tinyStore = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions
                {
                    RecoveryDocumentSizeCeilingBytes = 128
                });
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => tinyStore.CreateLifecycleAsync(
                    Guid.NewGuid().ToString("D"),
                    clock.UtcNow.AddDays(45),
                    CancellationToken.None));

            var listId = Guid.NewGuid();
            var channelId = $"UC-large-{Guid.NewGuid():N}";
            var oversized = CreateChannelDocument(channelId, clock);
            oversized.Title = new string(
                'x',
                Constants.CosmosChannelSerializedSizeSafetyCeilingBytes);
            await Channels.CreateItemAsync(oversized, new PartitionKey(channelId));
            var repository = new CosmosChannelRepository(
                Channels,
                Lists,
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await Assert.ThrowsAsync<ListCapacityExceededException>(
                () => repository.ReserveSubscriptionAsync(
                    channelId,
                    listId,
                    CancellationToken.None));
            Assert.Empty((await ReadChannelAsync(channelId)).SubscribedListIds);
        }

        [CosmosFact]
        public async Task RemoveRecoveryConvergesAfterEachDurablePartialState()
        {
            var clock = CreateClock();
            foreach (var stage in new[] { "edge", "list", "channel" })
            {
                var listId = Guid.NewGuid();
                var channelId = $"UC-remove-{stage}-{Guid.NewGuid():N}";
                await SeedChannelAsync(channelId, clock);
                var repository = CreateListRepository(clock);
                await repository.CreateAsync(CreateList(listId, clock));
                await repository.AddChannelAsync(listId, channelId);
                var store = new CosmosRecoveryStore(
                    Recovery,
                    clock,
                    new CosmosRecoveryOptions());
                await store.ActivateCandidateAsync(
                    listId.ToString("D"),
                    channelId,
                    $"terminated-remove-{stage}",
                    CancellationToken.None);

                if (stage is "list" or "channel")
                {
                    var list = await ReadListAsync(listId);
                    list.Channels = Array.Empty<CosmosProjectedChannelDocument>();
                    list.MembershipVersion++;
                    list.MembershipRecoveryPending = true;
                    list.MembershipRecoveryDueAt = clock.UtcNow;
                    await Lists.ReplaceItemAsync(
                        list,
                        list.Id,
                        new PartitionKey(list.Id),
                        new ItemRequestOptions { IfMatchEtag = list.ETag });
                }

                if (stage == "channel")
                {
                    var channels = new CosmosChannelRepository(
                        Channels,
                        Lists,
                        Recovery,
                        clock,
                        new CosmosRecoveryOptions());
                    await channels.RepairSubscriptionFromListTruthAsync(
                        channelId,
                        listId,
                        CancellationToken.None);
                }

                clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                    .AddSeconds(1);
                await RunUntilConvergedAsync(clock, listId, channelId);

                var authoritative = (await ReadListAsync(listId)).Channels.Any(
                    channel => channel.Id == channelId);
                var channel = await ReadChannelAsync(channelId);
                Assert.Equal(
                    authoritative,
                    channel.SubscribedListIds.Contains(
                        listId.ToString("D"),
                        StringComparer.OrdinalIgnoreCase));
                if (authoritative)
                {
                    Assert.NotNull(await ReadEdgeAsync(listId, channelId));
                }
                else
                {
                    Assert.Null(await TryReadEdgeAsync(listId, channelId));
                }
            }
        }

        [CosmosFact]
        public async Task PoisonBackoffRemainsDurableAndLeaseCanBeTakenOver()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid().ToString("D");
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                listId,
                clock.UtcNow.AddDays(45),
                CancellationToken.None);
            var edge = await store.ActivateCandidateAsync(
                listId,
                $"UC-poison-{Guid.NewGuid():N}",
                "failed-owner",
                CancellationToken.None);
            clock.UtcNow = clock.UtcNow.Add(Constants.ConsistencyRecoveryLeaseDuration)
                .AddSeconds(1);
            edge = await store.ClaimEdgeAsync(
                listId,
                edge.Id,
                "replacement-owner",
                CancellationToken.None);
            Assert.NotNull(edge);
            Assert.Equal("replacement-owner", edge.Owner);
            Assert.True(edge.LeaseTakenOver);
            for (var attempt = 0; attempt < Constants.ConsistencyRecoveryPoisonAttemptCount; attempt++)
            {
                edge = await store.ReadEdgeAsync(listId, edge.Id, CancellationToken.None);
                await store.FailEdgeAsync(
                    edge,
                    new InvalidOperationException("sanitized failure"),
                    CancellationToken.None);
            }

            edge = await store.ReadEdgeAsync(listId, edge.Id, CancellationToken.None);
            Assert.Equal("Poison", edge.State);
            Assert.Equal(Constants.ConsistencyRecoveryPoisonAttemptCount, edge.Attempt);
            Assert.Equal("InvalidOperationException", edge.LastErrorClass);
            Assert.Equal(
                clock.UtcNow.Add(Constants.ConsistencyRecoveryPoisonBackoff),
                edge.NextAttemptAt);

            clock.UtcNow = edge.NextAttemptAt.Value;
            var takenOver = await store.ClaimEdgeAsync(
                listId,
                edge.Id,
                "retry-owner",
                CancellationToken.None);
            Assert.NotNull(takenOver);
            Assert.Equal("retry-owner", takenOver.Owner);
        }

        [CosmosFact]
        public async Task MembershipAndProjectionFailuresBackOffBecomePoisonAndRetryDurably()
        {
            using var metrics = new RecordingMeterListener(
                CosmosRecoveryTelemetry.MeterName);
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var secretToken = "super-secret-list-token";
            var list = CosmosDocumentMapper.ToDocument(
                new SubscriptionList
                {
                    Id = listId,
                    Token = Encoding.UTF8.GetBytes(secretToken),
                    Title = "failure-list",
                    ExpiredAfter = clock.UtcNow.AddDays(45)
                },
                clock.UtcNow);
            list.MembershipVersion = 1;
            list.MembershipRecoveryPending = true;
            list.MembershipRecoveryStartedAt = clock.UtcNow;
            list.MembershipRecoveryDueAt = clock.UtcNow.AddDays(-10_000);
            await Lists.CreateItemAsync(list, new PartitionKey(list.Id));
            var channelId = $"UC-failure-{Guid.NewGuid():N}";
            var channel = CreateChannelDocument(channelId, clock);
            channel.ProjectionVersion = 1;
            channel.ProjectionRecoveryPending = true;
            channel.ProjectionRecoveryStartedAt = clock.UtcNow;
            channel.ProjectionRecoveryDueAt = clock.UtcNow.AddDays(-10_000);
            await Channels.CreateItemAsync(channel, new PartitionKey(channel.Id));
            var hooks = new CosmosRecoveryInterleavingHooks
            {
                BeforeMembershipWorkAsync = _ =>
                    throw new InvalidOperationException("membership injected"),
                BeforeProjectionWorkAsync = _ =>
                    throw new InvalidOperationException("projection injected")
            };
            var logger = new RecordingLogger<CosmosConsistencyRecoveryService>();

            for (var attempt = 1;
                attempt <= Constants.ConsistencyRecoveryPoisonAttemptCount;
                attempt++)
            {
                await DeleteRecoverySystemCursorsAsync();
                await CreateRecoveryService(clock, hooks, logger).RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    CancellationToken.None);
                var failedList = await ReadListAsync(listId);
                var failedChannel = await ReadChannelAsync(channelId);
                Assert.Equal(attempt, failedList.MembershipRecoveryAttempt);
                Assert.Equal(attempt, failedChannel.ProjectionRecoveryAttempt);
                Assert.Equal(
                    "InvalidOperationException",
                    failedList.MembershipRecoveryLastErrorClass);
                Assert.Equal(
                    "InvalidOperationException",
                    failedChannel.ProjectionRecoveryLastErrorClass);
                Assert.Equal(
                    attempt >= Constants.ConsistencyRecoveryPoisonAttemptCount,
                    failedList.MembershipRecoveryPoison);
                Assert.Equal(
                    attempt >= Constants.ConsistencyRecoveryPoisonAttemptCount,
                    failedChannel.ProjectionRecoveryPoison);
                clock.UtcNow = new[]
                {
                    failedList.MembershipRecoveryDueAt.Value,
                    failedChannel.ProjectionRecoveryDueAt.Value
                }.Max();
                if (attempt < Constants.ConsistencyRecoveryPoisonAttemptCount)
                {
                    failedList.MembershipRecoveryDueAt = clock.UtcNow.AddDays(-10_000);
                    await Lists.ReplaceItemAsync(
                        failedList,
                        failedList.Id,
                        new PartitionKey(failedList.Id),
                        new ItemRequestOptions { IfMatchEtag = failedList.ETag });
                    failedChannel.ProjectionRecoveryDueAt =
                        clock.UtcNow.AddDays(-10_000);
                    await Channels.ReplaceItemAsync(
                        failedChannel,
                        failedChannel.Id,
                        new PartitionKey(failedChannel.Id),
                        new ItemRequestOptions { IfMatchEtag = failedChannel.ETag });
                }
            }

            await DeleteRecoverySystemCursorsAsync();
            await CreateRecoveryService(clock).RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                CancellationToken.None);
            Assert.False((await ReadListAsync(listId)).MembershipRecoveryPending);
            Assert.False((await ReadChannelAsync(channelId)).ProjectionRecoveryPending);
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains(
                        "WorkKind=Membership",
                        StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Error
                    && entry.Message.Contains(
                        "WorkKind=Projection",
                        StringComparison.Ordinal));
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.pending.items"
                    && measurement.WorkKind == "Membership"
                    && measurement.Value == 1);
            Assert.Contains(
                metrics.Measurements,
                measurement => measurement.Name == "recovery.pending.items"
                    && measurement.WorkKind == "Projection"
                    && measurement.Value == 1);
            Assert.Contains(
                metrics.Measurements,
                measurement =>
                    measurement.Name == "recovery.convergence.latency"
                    && measurement.WorkKind == "Membership"
                    && measurement.Value > 0);
            Assert.Contains(
                metrics.Measurements,
                measurement =>
                    measurement.Name == "recovery.convergence.latency"
                    && measurement.WorkKind == "Projection"
                    && measurement.Value > 0);
            Assert.All(
                logger.Messages,
                message =>
                {
                    Assert.DoesNotContain(secretToken, message, StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        CosmosEmulatorOptions.DefaultConnectionString,
                        message,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        "C2FBB1BA8CFA",
                        message,
                        StringComparison.OrdinalIgnoreCase);
                });
        }

        [CosmosFact]
        public async Task ConcurrentInstancesSerializeDurableTicketAdmission()
        {
            var clock = CreateClock();
            for (var iteration = 0; iteration < 8; iteration++)
            {
                await DeleteRecoverySystemCursorsAsync();
                var results = await Task.WhenAll(
                    CreateRecoveryService(clock).RecoverAsync(
                        new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                        CancellationToken.None),
                    CreateRecoveryService(clock).RecoverAsync(
                        new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                        CancellationToken.None));
                var cursor = (await Recovery.ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                    CosmosRecoveryTicketCursorDocument.DocumentId,
                    new PartitionKey("__system"))).Resource;

                Assert.True(cursor.RotationGeneration >= 1);
                Assert.All(results, result => Assert.True(result.Examined <= 1));
            }
        }

        [CosmosFact]
        public async Task EmptyPassCompletionIsIndependentOfStartingRotationAndRestart()
        {
            var isolated = await CreateIsolatedRecoveryContainersAsync("empty-rotation");
            try
            {
                var clock = CreateClock();
                foreach (var startingKind in new[]
                {
                    "Membership",
                    "Projection",
                    "EdgeDue",
                    "LifecycleDue"
                })
                {
                    await ForceNextTicketAsync(
                        isolated.Recovery,
                        startingKind,
                        clock);
                    var before = (await isolated.Recovery
                        .ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                            CosmosRecoveryTicketCursorDocument.DocumentId,
                            new PartitionKey("__system"))).Resource;
                    var beforeGeneration = before.RotationGeneration;

                    await CreateRecoveryService(
                        isolated.Lists,
                        isolated.Channels,
                        isolated.Recovery,
                        clock).RecoverAsync(
                            new ConsistencyRecoveryPassBudget(25, 100, 2_000),
                            default);

                    var after = (await isolated.Recovery
                        .ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                            CosmosRecoveryTicketCursorDocument.DocumentId,
                            new PartitionKey("__system"))).Resource;
                    Assert.Equal(startingKind, after.NextStartingKind);
                    Assert.Equal(beforeGeneration + 4, after.RotationGeneration);
                }

                foreach (var startingKind in new[]
                {
                    "Projection",
                    "EdgeDue",
                    "LifecycleDue"
                })
                {
                    await TryDeleteAsync(
                        isolated.Recovery,
                        "cursor:membership",
                        "__system");
                    var listId = Guid.NewGuid();
                    var pending = CreateListDocument(
                        listId,
                        clock,
                        channelId: null,
                        membershipVersion: 1,
                        pending: true);
                    pending.Channels = Array.Empty<CosmosProjectedChannelDocument>();
                    pending.MembershipRecoveryStartedAt = clock.UtcNow.AddMinutes(-5);
                    await isolated.Lists.CreateItemAsync(
                        pending,
                        new PartitionKey(pending.Id));
                    await ForceNextTicketAsync(
                        isolated.Recovery,
                        startingKind,
                        clock);

                    await CreateRecoveryService(
                        isolated.Lists,
                        isolated.Channels,
                        isolated.Recovery,
                        clock).RecoverAsync(
                            new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                            default);

                    var repaired = (await isolated.Lists
                        .ReadItemAsync<CosmosListDocument>(
                            pending.Id,
                            new PartitionKey(pending.Id))).Resource;
                    Assert.False(repaired.MembershipRecoveryPending);
                }
            }
            finally
            {
                await isolated.Lists.DeleteContainerAsync();
                await isolated.Channels.DeleteContainerAsync();
                await isolated.Recovery.DeleteContainerAsync();
            }
        }

        [CosmosFact]
        public async Task ConcurrentInstancesResolveTheSamePerKindCursorConflict()
        {
            var isolated = await CreateIsolatedRecoveryContainersAsync("cursor-conflict");
            using var metrics = new RecordingMeterListener(
                CosmosRecoveryTelemetry.MeterName);
            try
            {
                var clock = CreateClock();
                var firstEntered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var secondEntered = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var firstHooks = new CosmosRecoveryInterleavingHooks
                {
                    BeforeCursorAdvanceAsync = async workKind =>
                    {
                        if (workKind == "Membership")
                        {
                            firstEntered.TrySetResult();
                            await release.Task;
                        }
                    }
                };
                var secondHooks = new CosmosRecoveryInterleavingHooks
                {
                    BeforeCursorAdvanceAsync = async workKind =>
                    {
                        if (workKind == "Membership")
                        {
                            secondEntered.TrySetResult();
                            await release.Task;
                        }
                    }
                };
                var logger = new RecordingLogger<CosmosConsistencyRecoveryService>();

                await ForceNextTicketAsync(isolated.Recovery, "Membership", clock);
                var first = CreateRecoveryService(
                    isolated.Lists,
                    isolated.Channels,
                    isolated.Recovery,
                    clock,
                    firstHooks,
                    logger).RecoverAsync(
                        new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                        default);
                await firstEntered.Task;

                await ForceNextTicketAsync(isolated.Recovery, "Membership", clock);
                var second = CreateRecoveryService(
                    isolated.Lists,
                    isolated.Channels,
                    isolated.Recovery,
                    clock,
                    secondHooks,
                    logger).RecoverAsync(
                        new ConsistencyRecoveryPassBudget(1, 1, 0.01),
                        default);
                await secondEntered.Task;
                release.SetResult();
                await Task.WhenAll(first, second);

                var cursor = await ReadCursorAsync(
                    isolated.Recovery,
                    "cursor:membership");
                Assert.True(cursor.CycleGeneration >= 2);
                Assert.Contains(
                    logger.Messages,
                    message => message.Contains(
                        "optimistic concurrency conflict",
                        StringComparison.Ordinal));
                Assert.Contains(
                    metrics.Measurements,
                    measurement =>
                        measurement.Name
                            == "recovery.persistence.etag_conflicts"
                        && measurement.Value >= 1);
            }
            finally
            {
                await isolated.Lists.DeleteContainerAsync();
                await isolated.Channels.DeleteContainerAsync();
                await isolated.Recovery.DeleteContainerAsync();
            }
        }

        [CosmosFact]
        public async Task CursorKeepsFixedCycleAdvancesTupleWrapsOnlyAtEndAndRevisitsBehindInsertion()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var names = new CosmosOptions
            {
                ListsContainer = $"cursor-lists-{suffix}",
                ChannelsContainer = $"cursor-channels-{suffix}",
                ShareLinksContainer = $"cursor-share-{suffix}",
                SystemContainer = $"cursor-system-{suffix}",
                RecoveryContainer = $"cursor-recovery-{suffix}"
            };
            var desired = CosmosContainerInitializer.GetContainerProperties(names);
            var lists = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.ListsContainer))).Container;
            var channels = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.ChannelsContainer))).Container;
            var recovery = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.RecoveryContainer))).Container;
            var clock = CreateClock();
            var store = new CosmosRecoveryStore(
                recovery,
                clock,
                new CosmosRecoveryOptions());
            var due1 = clock.UtcNow.AddMinutes(-3);
            var due2 = clock.UtcNow.AddMinutes(-2);
            var due3 = clock.UtcNow.AddMinutes(-1);
            await store.CreateLifecycleAsync(Guid.NewGuid().ToString("D"), due1, default);
            await store.CreateLifecycleAsync(Guid.NewGuid().ToString("D"), due2, default);
            await store.CreateLifecycleAsync(Guid.NewGuid().ToString("D"), due3, default);
            var service = new CosmosConsistencyRecoveryService(
                lists,
                channels,
                recovery,
                clock,
                new CosmosRecoveryOptions(),
                NullLogger<CosmosConsistencyRecoveryService>.Instance);

            await ForceNextTicketAsync(recovery, "LifecycleDue", clock);
            await service.RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                default);
            var cursor = await ReadCursorAsync(recovery, "cursor:lifecycle-due");
            var fixedCycleNow = cursor.CycleNow;
            Assert.Equal(due1, cursor.AfterDueAt);

            var behindId = Guid.NewGuid().ToString("D");
            await store.CreateLifecycleAsync(
                behindId,
                due1.AddMinutes(-1),
                default);
            await ForceNextTicketAsync(recovery, "LifecycleDue", clock);
            await new CosmosConsistencyRecoveryService(
                lists,
                channels,
                recovery,
                clock,
                new CosmosRecoveryOptions(),
                NullLogger<CosmosConsistencyRecoveryService>.Instance).RecoverAsync(
                    new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                    default);
            cursor = await ReadCursorAsync(recovery, "cursor:lifecycle-due");
            Assert.Equal(fixedCycleNow, cursor.CycleNow);
            Assert.Equal(due2, cursor.AfterDueAt);

            await ForceNextTicketAsync(recovery, "LifecycleDue", clock);
            await service.RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                default);
            cursor = await ReadCursorAsync(recovery, "cursor:lifecycle-due");
            Assert.Equal(due3, cursor.AfterDueAt);
            Assert.Equal(0, cursor.CycleGeneration);

            await ForceNextTicketAsync(recovery, "LifecycleDue", clock);
            await service.RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                default);
            cursor = await ReadCursorAsync(recovery, "cursor:lifecycle-due");
            Assert.Null(cursor.AfterDueAt);
            Assert.Equal(1, cursor.CycleGeneration);

            await ForceNextTicketAsync(recovery, "LifecycleDue", clock);
            await service.RecoverAsync(
                new ConsistencyRecoveryPassBudget(1, 1, 2_000),
                default);
            cursor = await ReadCursorAsync(recovery, "cursor:lifecycle-due");
            Assert.Equal(due1.AddMinutes(-1), cursor.AfterDueAt);
            Assert.Equal(behindId, cursor.AfterListId);

            await lists.DeleteContainerAsync();
            await channels.DeleteContainerAsync();
            await recovery.DeleteContainerAsync();
        }

        private static async Task ForceNextTicketAsync(
            Container recovery,
            string workKind,
            FakeAppClock clock)
        {
            try
            {
                var response = await recovery.ReadItemAsync<CosmosRecoveryTicketCursorDocument>(
                    CosmosRecoveryTicketCursorDocument.DocumentId,
                    new PartitionKey("__system"));
                var ticket = response.Resource;
                ticket.NextStartingKind = workKind;
                ticket.UpdatedAt = clock.UtcNow;
                await recovery.ReplaceItemAsync(
                    ticket,
                    ticket.Id,
                    new PartitionKey("__system"),
                    new ItemRequestOptions { IfMatchEtag = ticket.ETag });
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                await recovery.CreateItemAsync(
                    new CosmosRecoveryTicketCursorDocument
                    {
                        NextStartingKind = workKind,
                        UpdatedAt = clock.UtcNow
                    },
                    new PartitionKey("__system"));
            }
        }

        private static async Task<CosmosRecoveryCursorDocument> ReadCursorAsync(
            Container recovery,
            string id)
        {
            return (await recovery.ReadItemAsync<CosmosRecoveryCursorDocument>(
                id,
                new PartitionKey("__system"))).Resource;
        }

        private async Task<(
            Container Lists,
            Container Channels,
            Container Recovery)> CreateIsolatedRecoveryContainersAsync(string prefix)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var names = new CosmosOptions
            {
                ListsContainer = $"{prefix}-lists-{suffix}",
                ChannelsContainer = $"{prefix}-channels-{suffix}",
                ShareLinksContainer = $"{prefix}-share-{suffix}",
                SystemContainer = $"{prefix}-system-{suffix}",
                RecoveryContainer = $"{prefix}-recovery-{suffix}"
            };
            var desired = CosmosContainerInitializer.GetContainerProperties(names);
            var lists = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.ListsContainer))).Container;
            var channels = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.ChannelsContainer))).Container;
            var recovery = (await _fixture.Database.CreateContainerAsync(
                desired.Single(value => value.Id == names.RecoveryContainer))).Container;
            return (lists, channels, recovery);
        }

        private static async Task TryDeleteAsync(
            Container container,
            string id,
            string partitionKey)
        {
            try
            {
                await container.DeleteItemAsync<object>(
                    id,
                    new PartitionKey(partitionKey));
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        [CosmosFact]
        public async Task ExactDueQueriesReturnIndexMetricsAndMeasuredRu()
        {
            var clock = CreateClock();
            var listId = Guid.NewGuid();
            var channelId = $"UC-plan-{Guid.NewGuid():N}";
            await SeedChannelAsync(channelId, clock);
            var list = CreateListDocument(listId, clock, channelId, 1, true);
            await Lists.CreateItemAsync(list, new PartitionKey(list.Id));
            var channel = await ReadChannelAsync(channelId);
            channel.ProjectionVersion = 1;
            channel.ProjectionRecoveryPending = true;
            channel.ProjectionRecoveryDueAt = clock.UtcNow;
            await Channels.ReplaceItemAsync(
                channel,
                channel.Id,
                new PartitionKey(channel.Id),
                new ItemRequestOptions { IfMatchEtag = channel.ETag });
            var store = new CosmosRecoveryStore(
                Recovery,
                clock,
                new CosmosRecoveryOptions());
            await store.CreateLifecycleAsync(
                list.Id,
                clock.UtcNow,
                CancellationToken.None);
            var edge = await store.ActivateCandidateAsync(
                list.Id,
                channelId,
                "plan-owner",
                CancellationToken.None);
            edge.Owner = null;
            edge.LeaseUntil = null;
            edge.NextAttemptAt = clock.UtcNow;
            await Recovery.ReplaceItemAsync(
                edge,
                edge.Id,
                new PartitionKey(edge.ListId),
                new ItemRequestOptions { IfMatchEtag = edge.ETag });

            var options = new QueryRequestOptions
            {
                MaxItemCount = 25,
                PopulateIndexMetrics = true
            };
            var planCursor = new CosmosRecoveryCursorDocument
            {
                CycleNow = clock.UtcNow
            };
            var membership = await ReadOneQueryPageAsync<CosmosListDocument>(
                Lists,
                CosmosConsistencyRecoveryService.CreateMembershipDueQuery(planCursor),
                options);
            var projection = await ReadOneQueryPageAsync<CosmosChannelDocument>(
                Channels,
                CosmosConsistencyRecoveryService.CreateProjectionDueQuery(planCursor),
                options);
            var edges = await ReadOneQueryPageAsync<CosmosRecoveryEdgeDocument>(
                Recovery,
                CosmosConsistencyRecoveryService.CreateEdgeDueQuery(planCursor),
                options);
            var lifecycles = await ReadOneQueryPageAsync<CosmosRecoveryLifecycleDocument>(
                Recovery,
                CosmosConsistencyRecoveryService.CreateLifecycleDueQuery(planCursor),
                options);

            Assert.All(
                new[]
                {
                    (
                        membership.RequestCharge,
                        membership.IndexMetrics,
                        "\"IndexSpecs\":[\"/membershipRecoveryDueAt ASC\",\"/id ASC\"]"),
                    (
                        projection.RequestCharge,
                        projection.IndexMetrics,
                        "\"IndexSpecs\":[\"/projectionRecoveryDueAt ASC\",\"/id ASC\"]"),
                    (
                        edges.RequestCharge,
                        edges.IndexMetrics,
                        "\"IndexSpecs\":[\"/nextAttemptAt ASC\",\"/listId ASC\",\"/id ASC\"]"),
                    (
                        lifecycles.RequestCharge,
                        lifecycles.IndexMetrics,
                        "\"IndexSpecs\":[\"/nextCheckAt ASC\",\"/listId ASC\",\"/id ASC\"]")
                },
                response =>
                {
                    Assert.True(response.RequestCharge > 0);
                    Assert.False(string.IsNullOrWhiteSpace(response.IndexMetrics));
                    Assert.Contains(
                        response.Item3,
                        response.IndexMetrics,
                        StringComparison.Ordinal);
                });
        }

        private static async Task<FeedResponse<T>> ReadOneQueryPageAsync<T>(
            Container container,
            QueryDefinition query,
            QueryRequestOptions options)
        {
            using var iterator = container.GetItemQueryIterator<T>(
                query,
                requestOptions: options);
            return await iterator.ReadNextAsync();
        }

        private async Task RunUntilConvergedAsync(
            FakeAppClock clock,
            Guid listId,
            string channelId)
        {
            for (var pass = 0; pass < 12; pass++)
            {
                await CreateRecoveryService(clock).RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    CancellationToken.None);
                var list = await ReadListAsync(listId);
                var channel = await ReadChannelAsync(channelId);
                var authoritative = list.Channels.Any(value => value.Id == channelId);
                var reverse = channel.SubscribedListIds.Contains(
                    listId.ToString("D"),
                    StringComparer.OrdinalIgnoreCase);
                var edge = await TryReadEdgeAsync(listId, channelId);
                if (authoritative == reverse
                    && !list.MembershipRecoveryPending
                    && (authoritative ? edge != null : edge == null))
                {
                    return;
                }
            }
        }

        private async Task ReplenishMembershipAsync(
            Guid listId,
            FakeAppClock clock)
        {
            var list = await ReadListAsync(listId);
            list.MembershipVersion++;
            list.MembershipRecoveryPending = true;
            list.MembershipRecoveryDueAt = clock.UtcNow.AddDays(-100);
            await Lists.ReplaceItemAsync(
                list,
                list.Id,
                new PartitionKey(list.Id),
                new ItemRequestOptions { IfMatchEtag = list.ETag });
        }

        private async Task DeleteRecoverySystemCursorsAsync()
        {
            using var iterator = Recovery.GetItemQueryIterator<string>(
                "SELECT VALUE c.id FROM c",
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey("__system")
                });
            while (iterator.HasMoreResults)
            {
                foreach (var id in await iterator.ReadNextAsync())
                {
                    await Recovery.DeleteItemAsync<object>(
                        id,
                        new PartitionKey("__system"));
                }
            }
        }

        private async Task<int> CountRecoveryEdgesAsync(
            string listId,
            bool activeOnly)
        {
            var query = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.kind = \"Edge\""
                + (activeOnly ? " AND c.active = true" : string.Empty));
            using var iterator = Recovery.GetItemQueryIterator<int>(
                query,
                requestOptions: new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(listId),
                    MaxItemCount = 1
                });
            return (await iterator.ReadNextAsync()).Single();
        }

        private async Task SeedChannelAsync(string channelId, FakeAppClock clock)
        {
            var document = CreateChannelDocument(channelId, clock);
            await Channels.CreateItemAsync(document, new PartitionKey(channelId));
        }

        private async Task SeedLegacyListWithReverseReferencesAsync(
            Guid listId,
            IReadOnlyList<string> channelIds,
            FakeAppClock clock)
        {
            foreach (var channelId in channelIds)
            {
                await SeedChannelAsync(channelId, clock);
                var channel = await ReadChannelAsync(channelId);
                channel.SubscribedListIds = new[] { listId.ToString("D") };
                channel.SubscriptionCount = 1;
                channel.OrphanedAfter = null;
                channel.Ttl = -1;
                await Channels.ReplaceItemAsync(
                    channel,
                    channel.Id,
                    new PartitionKey(channel.Id),
                    new ItemRequestOptions { IfMatchEtag = channel.ETag });
            }

            var list = CreateListDocument(
                listId,
                clock,
                channelIds[0],
                membershipVersion: channelIds.Count,
                pending: false);
            list.Channels = channelIds.Select(channelId =>
                new CosmosProjectedChannelDocument
                {
                    Id = channelId,
                    Status = ChannelStatus.Active.ToString(),
                    StatusReason = ChannelStatusReason.None.ToString(),
                    Videos = Array.Empty<CosmosVideoDocument>()
                }).ToArray();
            await Lists.CreateItemAsync(list, new PartitionKey(list.Id));
        }

        private static CosmosChannelDocument CreateChannelDocument(
            string channelId,
            FakeAppClock clock)
        {
            return CosmosDocumentMapper.ToChannelDocument(
                new Channel
                {
                    Id = channelId,
                    Url = $"https://www.youtube.com/channel/{channelId}",
                    Title = "channel",
                    Thumbnail = "https://example.test/channel.jpg",
                    PlaylistId = $"UU-{channelId}",
                    StaleAfter = clock.UtcNow.AddHours(1),
                    Status = ChannelStatus.Active,
                    StatusReason = ChannelStatusReason.None,
                    OrphanedAfter = clock.UtcNow
                },
                clock.UtcNow,
                Constants.ChannelOrphanRetention);
        }

        private static SubscriptionList CreateList(Guid listId, FakeAppClock clock)
        {
            return new SubscriptionList
            {
                Id = listId,
                Token = new byte[] { 1, 2, 3, 4 },
                Title = "list",
                PlaybackRate = 1m,
                ExpiredAfter = clock.UtcNow.AddDays(45),
                ExpirationRenewedOn = clock.UtcToday
            };
        }

        private static CosmosListDocument CreateListDocument(
            Guid listId,
            FakeAppClock clock,
            string channelId,
            long membershipVersion,
            bool pending,
            string projectedTitle = "old-title")
        {
            var document = CosmosDocumentMapper.ToDocument(
                new SubscriptionList
                {
                    Id = listId,
                    Token = new byte[] { 5, 6, 7, 8 },
                    Title = "list",
                    PlaybackRate = 1m,
                    ExpiredAfter = clock.UtcNow.AddDays(45),
                    ExpirationRenewedOn = clock.UtcToday
                },
                clock.UtcNow);
            document.MembershipVersion = membershipVersion;
            document.MembershipRecoveryPending = pending;
            document.MembershipRecoveryDueAt = pending ? clock.UtcNow : null;
            document.Channels = new[]
            {
                new CosmosProjectedChannelDocument
                {
                    Id = channelId,
                    Url = $"https://www.youtube.com/channel/{channelId}",
                    Title = projectedTitle,
                    Thumbnail = "https://example.test/old.jpg",
                    StaleAfter = clock.UtcNow,
                    Status = ChannelStatus.Active.ToString(),
                    StatusReason = ChannelStatusReason.None.ToString()
                }
            };
            return document;
        }

        private CosmosListRepository CreateListRepository(
            FakeAppClock clock,
            CosmosRecoveryInterleavingHooks hooks = null,
            CosmosRecoveryOptions options = null)
        {
            return new CosmosListRepository(
                Lists,
                Channels,
                Recovery,
                clock,
                options ?? new CosmosRecoveryOptions(),
                new CosmosWorkerStateStore(System, clock),
                new InProcessWorkerWakeSignal(),
                hooks);
        }

        private CosmosConsistencyRecoveryService CreateRecoveryService(
            FakeAppClock clock,
            CosmosRecoveryInterleavingHooks hooks = null,
            ILogger<CosmosConsistencyRecoveryService> logger = null,
            CosmosRecoveryOptions options = null)
        {
            return new CosmosConsistencyRecoveryService(
                Lists,
                Channels,
                Recovery,
                clock,
                options ?? new CosmosRecoveryOptions(),
                logger ?? NullLogger<CosmosConsistencyRecoveryService>.Instance,
                hooks);
        }

        private static CosmosConsistencyRecoveryService CreateRecoveryService(
            Container lists,
            Container channels,
            Container recovery,
            FakeAppClock clock,
            CosmosRecoveryInterleavingHooks hooks = null,
            ILogger<CosmosConsistencyRecoveryService> logger = null)
        {
            return new CosmosConsistencyRecoveryService(
                lists,
                channels,
                recovery,
                clock,
                new CosmosRecoveryOptions(),
                logger ?? NullLogger<CosmosConsistencyRecoveryService>.Instance,
                hooks);
        }

        private CosmosListProjectionRepository CreateProjectionRepository(
            FakeAppClock clock,
            CosmosRecoveryInterleavingHooks hooks = null)
        {
            return new CosmosListProjectionRepository(
                Lists,
                Channels,
                Recovery,
                clock,
                new CosmosRecoveryOptions(),
                hooks);
        }

        private async Task<CosmosListDocument> ReadListAsync(Guid listId)
        {
            return (await Lists.ReadItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                new PartitionKey(listId.ToString("D")))).Resource;
        }

        private async Task<CosmosChannelDocument> ReadChannelAsync(string channelId)
        {
            return (await Channels.ReadItemAsync<CosmosChannelDocument>(
                channelId,
                new PartitionKey(channelId))).Resource;
        }

        private async Task<CosmosRecoveryLifecycleDocument> ReadLifecycleAsync(Guid listId)
        {
            return (await Recovery.ReadItemAsync<CosmosRecoveryLifecycleDocument>(
                CosmosRecoveryLifecycleDocument.DocumentId,
                new PartitionKey(listId.ToString("D")))).Resource;
        }

        private async Task<CosmosRecoveryLifecycleDocument> TryReadLifecycleAsync(Guid listId)
        {
            try
            {
                return await ReadLifecycleAsync(listId);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task<CosmosRecoveryEdgeDocument> ReadEdgeAsync(
            Guid listId,
            string channelId)
        {
            return (await Recovery.ReadItemAsync<CosmosRecoveryEdgeDocument>(
                CosmosRecoveryStore.GetEdgeId(channelId),
                new PartitionKey(listId.ToString("D")))).Resource;
        }

        private async Task<CosmosRecoveryEdgeDocument> TryReadEdgeAsync(
            Guid listId,
            string channelId)
        {
            try
            {
                return await ReadEdgeAsync(listId, channelId);
            }
            catch (CosmosException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private static async Task WaitForNotFoundAsync(
            Container container,
            string id,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await container.ReadItemAsync<object>(id, new PartitionKey(id));
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }

            throw new TimeoutException(
                $"Cosmos item '{id}' was not physically deleted within {timeout}.");
        }

        private static async Task<Exception> CaptureAsync(Func<Task> operation)
        {
            try
            {
                await operation();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static FakeAppClock CreateClock()
        {
            return new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)
            };
        }

        private Container Lists =>
            _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
        private Container Channels =>
            _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName);
        private Container Recovery =>
            _fixture.GetContainer(CosmosTestFixture.RecoveryContainerName);
        private Container System =>
            _fixture.GetContainer(CosmosTestFixture.SystemContainerName);

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            internal List<string> Messages { get; } = new();
            internal List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                var message = formatter(state, exception);
                Messages.Add(message);
                Entries.Add((logLevel, message));
            }

            private sealed class NullScope : IDisposable
            {
                internal static NullScope Instance { get; } = new();
                public void Dispose()
                {
                }
            }
        }

        private sealed class RecordingMeterListener : IDisposable
        {
            private readonly MeterListener _listener = new();
            private readonly object _gate = new();
            private readonly string _meterName;

            internal RecordingMeterListener(string meterName)
            {
                _meterName = meterName;
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == _meterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>(
                    (instrument, measurement, tags, _) =>
                        Add(instrument.Name, measurement, tags));
                _listener.SetMeasurementEventCallback<double>(
                    (instrument, measurement, tags, _) =>
                        Add(instrument.Name, measurement, tags));
                _listener.Start();
            }

            internal List<MetricMeasurement> Measurements { get; } = new();

            public void Dispose()
            {
                _listener.Dispose();
            }

            private void Add(
                string name,
                double value,
                ReadOnlySpan<KeyValuePair<string, object>> tags)
            {
                string workKind = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "work.kind")
                    {
                        workKind = tag.Value?.ToString();
                        break;
                    }
                }

                lock (_gate)
                {
                    Measurements.Add(new MetricMeasurement(name, value, workKind));
                }
            }
        }

        private sealed record MetricMeasurement(
            string Name,
            double Value,
            string WorkKind);
    }
}
