using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ChannelProviderContractTests : ProviderContractTestBase
    {
        protected ChannelProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
        }

        protected async Task CanonicalChannelCreateReadUpdateContractAsync()
        {
            var channel = await CreateChannelAsync("canonical-channel", title: "Original");
            var updated = ToDomainChannel(channel, new[] { CreateVideo(channel.Id) });
            updated.Title = "Updated";
            updated.Status = ChannelStatus.Unavailable;
            updated.StatusReason = ChannelStatusReason.NotFound;
            updated.StatusUpdatedAt = Clock.UtcNow;

            await Provider.Channels.SaveRefreshResultAsync(
                new ChannelRefreshResult
                {
                    Channel = updated,
                    VideosRefreshed = true,
                    EarliestPublishedAt = Clock.UtcNow.AddDays(-1)
                },
                CancellationToken.None);

            var persisted = await Provider.Channels.GetByIdAsync(channel.Id);
            Assert.Equal("Updated", persisted.Title);
            Assert.Equal(ChannelStatus.Unavailable, persisted.Status);
            Assert.Equal(ChannelStatusReason.NotFound, persisted.StatusReason);
            var batch = await Provider.Channels.GetBatchAsync(
                new[] { channel.Id },
                CancellationToken.None);
            Assert.Single(Assert.Single(batch).Videos);
        }

        protected async Task BatchReadReflectsCanonicalUpdatesContractAsync()
        {
            var channel = await CreateChannelAsync("batch-channel", title: "Before");
            var refreshed = ToDomainChannel(channel, Array.Empty<ChannelVideo>());
            refreshed.Title = "After";

            await Provider.Channels.SaveRefreshResultAsync(
                new ChannelRefreshResult { Channel = refreshed },
                CancellationToken.None);

            var batch = await Provider.Channels.GetBatchAsync(
                new[] { "missing-channel", channel.Id },
                CancellationToken.None);
            Assert.Equal("After", Assert.Single(batch).Title);
        }
    }
}
