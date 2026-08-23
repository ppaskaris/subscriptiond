using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.Services
{
    public sealed class ChannelRefreshQueueTests
    {
        [Fact]
        public async Task Queue_OrdersPriorityThenOldestStaleThenStableSequence()
        {
            using var queue = new ChannelRefreshQueue(10);
            var newer = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
            queue.Enqueue(new[]
            {
                Request("stale-newer", ChannelRefreshReason.Stale, newer),
                Request("forced", ChannelRefreshReason.Forced),
                Request("missing-1", ChannelRefreshReason.Missing),
                Request("stale-older", ChannelRefreshReason.Stale, newer.AddHours(-1)),
                Request("missing-2", ChannelRefreshReason.Missing)
            });

            var batch = await queue.DequeueBatchAsync(10, CancellationToken.None);

            Assert.Equal(
                new[] { "missing-1", "missing-2", "forced", "stale-older", "stale-newer" },
                batch.Select(request => request.ChannelId));
        }

        [Fact]
        public async Task Queue_DeduplicatesPromotesBoundsAndAllowsCompletedIdAgain()
        {
            using var queue = new ChannelRefreshQueue(2);
            Assert.True(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale)));
            Assert.True(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Missing)));
            Assert.True(queue.TryEnqueue(Request("channel-2", ChannelRefreshReason.Stale)));
            Assert.False(queue.TryEnqueue(Request("channel-3", ChannelRefreshReason.Missing)));

            var batch = await queue.DequeueBatchAsync(2, CancellationToken.None);

            Assert.Equal(ChannelRefreshReason.Missing, batch[0].Reason);
            Assert.False(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale)));
            queue.Complete(batch.Select(request => request.ChannelId).ToList());
            Assert.True(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale)));
        }

        [Fact]
        public async Task Queue_ForceDuringFlightCreatesOneFollowUpAndCoalescesRepeats()
        {
            using var queue = new ChannelRefreshQueue(2);
            queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale));
            var first = Assert.Single(await queue.DequeueBatchAsync(2, CancellationToken.None));

            Assert.False(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale)));
            Assert.True(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Forced)));
            Assert.False(queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Forced)));
            queue.Complete(new[] { first.ChannelId });

            var followUp = Assert.Single(await queue.DequeueBatchAsync(2, CancellationToken.None));
            Assert.Equal(ChannelRefreshReason.Forced, followUp.Reason);
        }

        [Fact]
        public async Task DequeueBatchAsync_WakesWhenWorkArrivesAndDrainsOnlyMaximum()
        {
            using var queue = new ChannelRefreshQueue(3);
            var pending = queue.DequeueBatchAsync(2, CancellationToken.None);
            Assert.False(pending.IsCompleted);

            queue.Enqueue(new[]
            {
                Request("channel-1", ChannelRefreshReason.Stale),
                Request("channel-2", ChannelRefreshReason.Stale),
                Request("channel-3", ChannelRefreshReason.Stale)
            });

            Assert.Equal(2, (await pending).Count);
            Assert.Equal("channel-3", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)).ChannelId);
        }

        [Fact]
        public async Task DequeueBatchAsync_ObservesCancellation()
        {
            using var queue = new ChannelRefreshQueue(2);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queue.DequeueBatchAsync(1, cancellation.Token));
        }

        [Fact]
        public async Task DequeueBatchAsync_CancellationDuringCoalescingPreservesWakeUp()
        {
            using var queue = new ChannelRefreshQueue(2, TimeSpan.FromMilliseconds(20));
            queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queue.DequeueBatchAsync(2, cancellation.Token));

            Assert.Equal("channel-1", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)).ChannelId);
        }

        [Fact]
        public async Task RefreshService_SelectivelyRequeuesTransientOutcomes()
        {
            using var queue = new ChannelRefreshQueue(2);
            queue.Enqueue(new[]
            {
                Request("completed", ChannelRefreshReason.Stale),
                Request("retry", ChannelRefreshReason.Stale)
            });
            var service = new ChannelRefreshHostedService(
                queue,
                new SelectivePipeline(),
                Options.Create(new YoutubeSyncOptions { CohortSize = 2 }),
                NullLogger<ChannelRefreshHostedService>.Instance);

            Assert.True(await service.RunOnceAsync(CancellationToken.None));
            Assert.Equal("retry", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)).ChannelId);
            Assert.Equal(1, queue.Count);
        }

        [Fact]
        public async Task RefreshService_StopCancelsBlockedDequeueAndCompletes()
        {
            using var queue = new ChannelRefreshQueue(2);
            var service = CreateHostedService(queue, new SelectivePipeline());
            await service.StartAsync(CancellationToken.None);
            await Task.Yield();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await service.StopAsync(shutdown.Token);

            Assert.True(service.ExecuteTask.IsCompleted);
        }

        [Fact]
        public async Task RefreshService_StopDuringPipelineRequeuesInFlightWork()
        {
            using var queue = new ChannelRefreshQueue(2);
            queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale));
            var pipeline = new BlockingPipeline();
            var service = CreateHostedService(queue, pipeline);
            await service.StartAsync(CancellationToken.None);
            await pipeline.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await service.StopAsync(shutdown.Token);

            Assert.Equal("channel-1", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)).ChannelId);
        }

        [Fact]
        public async Task RefreshService_StopCancelsErrorDelayAndKeepsFailedWorkQueued()
        {
            using var queue = new ChannelRefreshQueue(2);
            queue.TryEnqueue(Request("channel-1", ChannelRefreshReason.Stale));
            var pipeline = new FailingPipeline();
            var service = CreateHostedService(queue, pipeline);
            await service.StartAsync(CancellationToken.None);
            await pipeline.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await service.StopAsync(shutdown.Token);

            Assert.Equal("channel-1", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)).ChannelId);
        }

        private static ChannelRefreshHostedService CreateHostedService(
            IChannelRefreshQueue queue,
            IChannelRefreshPipeline pipeline)
        {
            return new ChannelRefreshHostedService(
                queue,
                pipeline,
                Options.Create(new YoutubeSyncOptions { CohortSize = 2 }),
                NullLogger<ChannelRefreshHostedService>.Instance);
        }

        private static ChannelRefreshRequest Request(
            string id,
            ChannelRefreshReason reason,
            DateTimeOffset? staleAfter = null) => new(id, reason, staleAfter);

        private sealed class SelectivePipeline : IChannelRefreshPipeline
        {
            public Task<ChannelRefreshPipelineResult> RefreshAsync(
                IReadOnlyCollection<ChannelRefreshRequest> requests,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new ChannelRefreshPipelineResult
                {
                    Outcomes = requests.Select(request => new ChannelRefreshOutcome(
                        request.ChannelId,
                        request.ChannelId == "retry"
                            ? ChannelRefreshDisposition.RetryTransient
                            : ChannelRefreshDisposition.Refreshed,
                        0,
                        0)).ToList()
                });
            }
        }

        private sealed class BlockingPipeline : IChannelRefreshPipeline
        {
            public TaskCompletionSource Entered { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<ChannelRefreshPipelineResult> RefreshAsync(
                IReadOnlyCollection<ChannelRefreshRequest> requests,
                CancellationToken cancellationToken)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
        }

        private sealed class FailingPipeline : IChannelRefreshPipeline
        {
            public TaskCompletionSource Invoked { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<ChannelRefreshPipelineResult> RefreshAsync(
                IReadOnlyCollection<ChannelRefreshRequest> requests,
                CancellationToken cancellationToken)
            {
                Invoked.TrySetResult();
                throw new InvalidOperationException("Injected refresh failure.");
            }
        }
    }
}
