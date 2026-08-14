using Microsoft.Extensions.Logging.Abstractions;
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
        public async Task Queue_DeduplicatesBoundsDrainsAndAllowsCompletedIdAgain()
        {
            using var queue = new ChannelRefreshQueue(2);
            Assert.True(queue.TryEnqueue("channel-1"));
            Assert.False(queue.TryEnqueue("channel-1"));
            Assert.True(queue.TryEnqueue("channel-2"));
            Assert.False(queue.TryEnqueue("channel-3"));

            var batch = await queue.DequeueBatchAsync(2, CancellationToken.None);

            Assert.Equal(new[] { "channel-1", "channel-2" }, batch);
            Assert.False(queue.TryEnqueue("channel-1"));
            queue.Complete(batch);
            Assert.True(queue.TryEnqueue("channel-1"));
        }

        [Fact]
        public async Task DequeueBatchAsync_WakesWhenWorkArrives()
        {
            using var queue = new ChannelRefreshQueue(2);
            var pending = queue.DequeueBatchAsync(2, CancellationToken.None);

            Assert.False(pending.IsCompleted);
            queue.TryEnqueue("channel-1");

            Assert.Equal("channel-1", Assert.Single(await pending));
        }

        [Fact]
        public async Task DequeueBatchAsync_DrainsOnlyRequestedMaximumAndLeavesRemainderReady()
        {
            using var queue = new ChannelRefreshQueue(3);
            queue.TryEnqueue("channel-1");
            queue.TryEnqueue("channel-2");
            queue.TryEnqueue("channel-3");

            Assert.Equal(new[] { "channel-1", "channel-2" },
                await queue.DequeueBatchAsync(2, CancellationToken.None));
            Assert.Equal("channel-3", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)));
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
        public async Task RefreshService_RequeuesBatchAfterFailure()
        {
            using var queue = new ChannelRefreshQueue(2);
            queue.TryEnqueue("channel-1");
            var service = new ChannelRefreshHostedService(
                queue,
                new FailingPipeline(),
                NullLogger<ChannelRefreshHostedService>.Instance);

            Assert.False(await service.RunOnceAsync(CancellationToken.None));
            Assert.Equal("channel-1", Assert.Single(
                await queue.DequeueBatchAsync(2, CancellationToken.None)));
        }

        private sealed class FailingPipeline : IChannelRefreshPipeline
        {
            public Task<ChannelRefreshPipelineResult> RefreshAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Expected test failure.");
            }
        }
    }
}
