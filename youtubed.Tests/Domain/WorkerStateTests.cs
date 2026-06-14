using System;
using Xunit;
using youtubed.Domain;

namespace youtubed.Tests.Domain
{
    public sealed class WorkerStateTests
    {
        [Fact]
        public void IsChannelRefreshDue_ReturnsFalseWhenNoActiveChannelWorkIsKnown()
        {
            var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
            var state = new WorkerState
            {
                NextChannelRefreshAt = null
            };

            Assert.False(state.IsChannelRefreshDue(now));
        }

        [Fact]
        public void IsChannelRefreshDue_ReturnsTrueForForcedOrDueRefresh()
        {
            var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

            Assert.True(new WorkerState { NextChannelRefreshAt = DateTimeOffset.MinValue }.IsChannelRefreshDue(now));
            Assert.True(new WorkerState { NextChannelRefreshAt = now }.IsChannelRefreshDue(now));
            Assert.True(new WorkerState { NextChannelRefreshAt = now.AddTicks(-1) }.IsChannelRefreshDue(now));
        }

        [Fact]
        public void IsChannelRefreshDue_ReturnsFalseForFutureRefresh()
        {
            var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
            var state = new WorkerState
            {
                NextChannelRefreshAt = now.AddTicks(1)
            };

            Assert.False(state.IsChannelRefreshDue(now));
        }

        [Fact]
        public void IsPurgeDue_ReturnsTrueAtOrAfterNextPurge()
        {
            var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

            Assert.True(new WorkerState { NextPurgeAt = now }.IsPurgeDue(now));
            Assert.True(new WorkerState { NextPurgeAt = now.AddTicks(-1) }.IsPurgeDue(now));
            Assert.False(new WorkerState { NextPurgeAt = now.AddTicks(1) }.IsPurgeDue(now));
        }
    }
}
