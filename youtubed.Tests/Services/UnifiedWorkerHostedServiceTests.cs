using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class UnifiedWorkerHostedServiceTests
    {
        [Fact]
        public async Task RunPassAsync_PurgeDueRunsPurgerAndSchedulesFixedInterval()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var workerState = new RecordingWorkerStateStore();
            var purger = new RecordingExpirationPurger
            {
                ExpiredListCount = 1,
                ExpiredShareLinkCount = 2,
                ExpiredChannelCount = 3
            };
            var service = CreateService(
                workerState,
                purger,
                new RecordingChannelRefreshPipeline(),
                new FakeAppClock { UtcNow = now });

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = null,
                    NextPurgeAt = now
                },
                CancellationToken.None);

            Assert.True(result.PurgeRan);
            Assert.Equal(1, purger.PurgeExpiredListsCallCount);
            Assert.Equal(1, purger.PurgeExpiredShareLinksCallCount);
            Assert.Equal(1, purger.PurgeExpiredChannelsCallCount);
            Assert.Equal(now.Add(Constants.PurgeInterval), workerState.CompletedNextPurgeAt);
            Assert.Equal(now.Add(Constants.PurgeInterval), result.NextPurgeAt);
        }

        [Fact]
        public async Task RunPassAsync_ChannelDueWithNoWorkClearsRefreshState()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var observed = DateTimeOffset.MinValue;
            var workerState = new RecordingWorkerStateStore();
            var pipeline = new RecordingChannelRefreshPipeline
            {
                Result = new ChannelRefreshPipelineResult()
            };
            var service = CreateService(
                workerState,
                new RecordingExpirationPurger(),
                pipeline,
                new FakeAppClock { UtcNow = now });

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = observed,
                    ChannelRefreshForceCount = 7,
                    NextPurgeAt = now.AddHours(1)
                },
                CancellationToken.None);

            Assert.Equal(1, pipeline.RefreshCallCount);
            Assert.Equal(observed, workerState.CompletedObservedNextChannelRefreshAt);
            Assert.Equal(7, workerState.CompletedObservedChannelRefreshForceCount);
            Assert.Null(workerState.CompletedNextChannelRefreshAt);
            Assert.Null(result.NextChannelRefreshAt);
        }

        [Fact]
        public async Task RunPassAsync_ChannelDueSchedulesNextRefreshFromPipeline()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var next = now.AddMinutes(60);
            var observed = DateTimeOffset.MinValue;
            var workerState = new RecordingWorkerStateStore();
            var pipeline = new RecordingChannelRefreshPipeline
            {
                Result = new ChannelRefreshPipelineResult
                {
                    StaleLookaheadCount = 1,
                    SelectedChannelCount = 1,
                    NextChannelRefreshAt = next
                }
            };
            var service = CreateService(
                workerState,
                new RecordingExpirationPurger(),
                pipeline,
                new FakeAppClock { UtcNow = now });

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = observed,
                    NextPurgeAt = now.AddHours(1)
                },
                CancellationToken.None);

            Assert.Equal(next, workerState.CompletedNextChannelRefreshAt);
            Assert.Equal(next, result.NextChannelRefreshAt);
        }

        [Fact]
        public async Task RunPassAsync_PurgeFailureStillRunsRemainingPurgePhases()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var workerState = new RecordingWorkerStateStore();
            var purger = new RecordingExpirationPurger
            {
                ThrowExpiredListPurge = true,
                ExpiredShareLinkCount = 2,
                ExpiredChannelCount = 3
            };
            var service = CreateService(
                workerState,
                purger,
                new RecordingChannelRefreshPipeline(),
                new FakeAppClock { UtcNow = now });

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = null,
                    NextPurgeAt = now
                },
                CancellationToken.None);

            Assert.True(result.PurgeRan);
            Assert.Equal(1, purger.PurgeExpiredListsCallCount);
            Assert.Equal(1, purger.PurgeExpiredShareLinksCallCount);
            Assert.Equal(1, purger.PurgeExpiredChannelsCallCount);
            Assert.Equal(0, result.ExpiredListDeleteCount);
            Assert.Equal(2, result.ExpiredShareLinkDeleteCount);
            Assert.Equal(3, result.ExpiredChannelDeleteCount);
            Assert.Equal(now.Add(Constants.PurgeInterval), workerState.CompletedNextPurgeAt);
        }

        [Fact]
        public async Task RunPassAsync_CanceledChannelRefreshKeepsRefreshDue()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var observed = DateTimeOffset.MinValue;
            var workerState = new RecordingWorkerStateStore();
            var pipeline = new RecordingChannelRefreshPipeline
            {
                Result = new ChannelRefreshPipelineResult
                {
                    StaleLookaheadCount = 5,
                    SelectedChannelCount = 2,
                    CanceledDuringYoutubeWork = true
                }
            };
            var service = CreateService(
                workerState,
                new RecordingExpirationPurger(),
                pipeline,
                new FakeAppClock { UtcNow = now });

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = observed,
                    NextPurgeAt = now.AddHours(1)
                },
                CancellationToken.None);

            Assert.Equal(now, workerState.CompletedNextChannelRefreshAt);
            Assert.Equal(now, result.NextChannelRefreshAt);
        }

        [Fact]
        public async Task SleepUntilNextWorkAsync_WakesWhenSignalIsPulsedAfterObservation()
        {
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var wakeSignal = new InProcessWorkerWakeSignal();
            var service = CreateService(
                new RecordingWorkerStateStore(),
                new RecordingExpirationPurger(),
                new RecordingChannelRefreshPipeline(),
                new FakeAppClock { UtcNow = now },
                wakeSignal);
            var observedVersion = wakeSignal.Version;
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));

            var wait = service.SleepUntilNextWorkAsync(
                new WorkerState
                {
                    NextChannelRefreshAt = null,
                    NextPurgeAt = now.AddHours(1)
                },
                observedVersion,
                cancellationTokenSource.Token);
            wakeSignal.Pulse();

            Assert.True(await wait);
        }

        [Fact]
        public async Task RunPassAsync_RecoveryDueRunsBeforeChannelAndSchedulesImmediateBacklog()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var workerState = new RecordingWorkerStateStore();
            var recovery = new RecordingConsistencyRecoveryService
            {
                Result = new ConsistencyRecoveryPassResult(
                    25,
                    20,
                    19,
                    1,
                    0,
                    123.5,
                    true,
                    now.AddMinutes(1))
            };
            var service = CreateService(
                workerState,
                new RecordingExpirationPurger(),
                new RecordingChannelRefreshPipeline(),
                new FakeAppClock { UtcNow = now },
                recovery: recovery);

            var result = await service.RunPassAsync(
                new WorkerState
                {
                    NextPurgeAt = now.AddHours(1),
                    NextConsistencyRecoveryAt = DateTimeOffset.MinValue,
                    ConsistencyRecoveryForceCount = 4
                },
                CancellationToken.None);

            Assert.Equal(1, recovery.CallCount);
            Assert.Equal(DateTimeOffset.MinValue, workerState.CompletedObservedRecoveryAt);
            Assert.Equal(4, workerState.CompletedObservedRecoveryForceCount);
            Assert.Equal(now, workerState.CompletedNextRecoveryAt);
            Assert.Equal(recovery.Result, result.ConsistencyRecovery);
        }

        private static UnifiedWorkerHostedService CreateService(
            RecordingWorkerStateStore workerStateStore,
            RecordingExpirationPurger expirationPurger,
            RecordingChannelRefreshPipeline channelRefreshPipeline,
            FakeAppClock clock,
            IWorkerWakeSignal wakeSignal = null,
            IConsistencyRecoveryService recovery = null)
        {
            return new UnifiedWorkerHostedService(
                workerStateStore,
                expirationPurger,
                recovery ?? new SqlConsistencyRecoveryService(),
                channelRefreshPipeline,
                wakeSignal ?? new InProcessWorkerWakeSignal(),
                clock,
                Mock.Of<ILogger<UnifiedWorkerHostedService>>());
        }

        private sealed class RecordingWorkerStateStore : IWorkerStateStore
        {
            public DateTimeOffset? CompletedObservedNextChannelRefreshAt { get; private set; }
            public long? CompletedObservedChannelRefreshForceCount { get; private set; }
            public DateTimeOffset? CompletedNextChannelRefreshAt { get; private set; }
            public DateTimeOffset? CompletedNextPurgeAt { get; private set; }
            public DateTimeOffset? CompletedObservedRecoveryAt { get; private set; }
            public long? CompletedObservedRecoveryForceCount { get; private set; }
            public DateTimeOffset? CompletedNextRecoveryAt { get; private set; }

            public Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public Task ForceChannelRefreshAsync(CancellationToken cancellationToken)
            {
                throw new NotImplementedException();
            }

            public Task ForceConsistencyRecoveryAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task CompleteChannelRefreshPassAsync(
                DateTimeOffset? observedNextChannelRefreshAt,
                long observedChannelRefreshForceCount,
                DateTimeOffset? nextChannelRefreshAt,
                CancellationToken cancellationToken)
            {
                CompletedObservedNextChannelRefreshAt = observedNextChannelRefreshAt;
                CompletedObservedChannelRefreshForceCount = observedChannelRefreshForceCount;
                CompletedNextChannelRefreshAt = nextChannelRefreshAt;
                return Task.CompletedTask;
            }

            public Task CompletePurgeAsync(
                DateTimeOffset nextPurgeAt,
                CancellationToken cancellationToken)
            {
                CompletedNextPurgeAt = nextPurgeAt;
                return Task.CompletedTask;
            }

            public Task CompleteConsistencyRecoveryPassAsync(
                DateTimeOffset observedNextConsistencyRecoveryAt,
                long observedConsistencyRecoveryForceCount,
                DateTimeOffset nextConsistencyRecoveryAt,
                CancellationToken cancellationToken)
            {
                CompletedObservedRecoveryAt = observedNextConsistencyRecoveryAt;
                CompletedObservedRecoveryForceCount = observedConsistencyRecoveryForceCount;
                CompletedNextRecoveryAt = nextConsistencyRecoveryAt;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingConsistencyRecoveryService :
            IConsistencyRecoveryService
        {
            public ConsistencyRecoveryPassResult Result { get; set; } =
                ConsistencyRecoveryPassResult.Empty;

            public int CallCount { get; private set; }

            public Task<ConsistencyRecoveryPassResult> RecoverAsync(
                ConsistencyRecoveryPassBudget budget,
                CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(Result);
            }
        }

        private sealed class RecordingExpirationPurger : IExpirationPurger
        {
            public int ExpiredListCount { get; set; }
            public int ExpiredShareLinkCount { get; set; }
            public int ExpiredChannelCount { get; set; }
            public bool ThrowExpiredListPurge { get; set; }
            public int PurgeExpiredListsCallCount { get; private set; }
            public int PurgeExpiredShareLinksCallCount { get; private set; }
            public int PurgeExpiredChannelsCallCount { get; private set; }

            public Task<int> PurgeExpiredListsAsync(CancellationToken cancellationToken)
            {
                PurgeExpiredListsCallCount++;
                if (ThrowExpiredListPurge)
                {
                    throw new InvalidOperationException("List purge failed.");
                }

                return Task.FromResult(ExpiredListCount);
            }

            public Task<int> PurgeExpiredShareLinksAsync(CancellationToken cancellationToken)
            {
                PurgeExpiredShareLinksCallCount++;
                return Task.FromResult(ExpiredShareLinkCount);
            }

            public Task<int> PurgeExpiredChannelsAsync(CancellationToken cancellationToken)
            {
                PurgeExpiredChannelsCallCount++;
                return Task.FromResult(ExpiredChannelCount);
            }
        }

        private sealed class RecordingChannelRefreshPipeline : IChannelRefreshPipeline
        {
            public ChannelRefreshPipelineResult Result { get; set; } =
                new ChannelRefreshPipelineResult();

            public int RefreshCallCount { get; private set; }

            public Task<ChannelRefreshPipelineResult> RefreshStaleChannelsAsync(
                CancellationToken cancellationToken)
            {
                RefreshCallCount++;
                return Task.FromResult(Result);
            }
        }
    }
}
