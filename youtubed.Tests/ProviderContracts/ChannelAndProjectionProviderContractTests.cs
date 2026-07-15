using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ChannelAndProjectionProviderContractTests : ProviderContractTestBase
    {
        protected ChannelAndProjectionProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
        }

        protected async Task CanonicalChannelCreateReadUpdateContractAsync()
        {
            var staleAfter = Clock.UtcNow.AddMinutes(-5);
            var channel = await CreateChannelAsync(
                id: "canonical-channel",
                title: "Original",
                playlistId: "original-playlist",
                staleAfter: staleAfter);

            var created = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { "missing-channel", channel.Id },
                CancellationToken.None));
            Assert.Equal(channel.Id, created.Id);
            Assert.Equal(channel.Url, created.Url);
            Assert.Equal("Original", created.Title);
            Assert.Equal(channel.Thumbnail, created.Thumbnail);
            Assert.Equal("original-playlist", created.PlaylistId);
            Assert.Equal(staleAfter, created.StaleAfter);
            Assert.Equal(ChannelStatus.Active, created.Status);
            Assert.Equal(ChannelStatusReason.None, created.StatusReason);
            Assert.Null(created.StatusUpdatedAt);
            Assert.Empty(created.SubscribedListIds);
            Assert.Equal(0, created.SubscriptionCount);
            Assert.Empty(created.Videos);

            var statusUpdatedAt = Clock.UtcNow.AddMinutes(1);
            var updatedStaleAfter = Clock.UtcNow.AddYears(100);
            channel.Url = "https://www.youtube.com/@canonical-channel";
            channel.Title = "Updated";
            channel.Thumbnail = "updated.png";
            channel.PlaylistId = "updated-playlist";
            channel.StaleAfter = updatedStaleAfter;
            channel.Status = ChannelStatus.Unavailable;
            channel.StatusReason = ChannelStatusReason.NotFound;
            channel.StatusUpdatedAt = statusUpdatedAt;
            var video = CreateVideo(channel.Id, "canonical-video", "Updated Video");
            var updated = ToDomainChannel(channel, new[] { video });

            await Provider.Channels.SaveRefreshResultsAsync(
                new[]
                {
                    new ChannelRefreshResult
                    {
                        Channel = updated,
                        VideosRefreshed = true,
                        EarliestPublishedAt = video.PublishedAt
                    }
                },
                CancellationToken.None);

            var persisted = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { channel.Id },
                CancellationToken.None));
            Assert.Equal(updated.Url, persisted.Url);
            Assert.Equal(updated.Title, persisted.Title);
            Assert.Equal(updated.Thumbnail, persisted.Thumbnail);
            Assert.Equal(updated.PlaylistId, persisted.PlaylistId);
            Assert.Equal(updatedStaleAfter, persisted.StaleAfter);
            Assert.Equal(ChannelStatus.Unavailable, persisted.Status);
            Assert.Equal(ChannelStatusReason.NotFound, persisted.StatusReason);
            Assert.Equal(statusUpdatedAt, persisted.StatusUpdatedAt);
            var persistedVideo = Assert.Single(persisted.Videos);
            Assert.Equal(video.VideoId, persistedVideo.VideoId);
            Assert.Equal(video.Title, persistedVideo.Title);
        }

        protected async Task StaleLookaheadContractAsync()
        {
            var list = await CreateListAsync();
            var oldest = await CreateChannelAsync("stale-oldest", staleAfter: Clock.UtcNow.AddMinutes(-10));
            var tiedA = await CreateChannelAsync("stale-a", staleAfter: Clock.UtcNow.AddMinutes(-5));
            var tiedB = await CreateChannelAsync("stale-b", staleAfter: Clock.UtcNow.AddMinutes(-5));
            var fresh = await CreateChannelAsync("fresh", staleAfter: Clock.UtcNow.AddMinutes(5));
            await CreateChannelAsync("orphan", staleAfter: Clock.UtcNow.AddMinutes(-20));
            await AddChannelToListAsync(list.Id, oldest.Id);
            await AddChannelToListAsync(list.Id, tiedA.Id);
            await AddChannelToListAsync(list.Id, tiedB.Id);
            await AddChannelToListAsync(list.Id, fresh.Id);

            var stale = await Provider.Channels.GetStaleLookaheadAsync(
                Clock.UtcNow,
                2,
                CancellationToken.None);

            Assert.Equal(new[] { oldest.Id, tiedA.Id }, stale.Select(item => item.Id));
            Assert.Equal(
                new[] { oldest.StaleAfter, tiedA.StaleAfter },
                stale.Select(item => item.StaleAfter));
            Assert.Equal(
                oldest.StaleAfter,
                await Provider.Channels.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None));
        }

        protected async Task UnavailableChannelsAreExcludedFromRefreshContractAsync()
        {
            var list = await CreateListAsync();
            var active = await CreateChannelAsync("active-due", staleAfter: Clock.UtcNow.AddMinutes(-2));
            var unavailable = await CreateChannelAsync("unavailable-due", staleAfter: Clock.UtcNow.AddMinutes(-10));
            await AddChannelToListAsync(list.Id, active.Id);
            await AddChannelToListAsync(list.Id, unavailable.Id);

            unavailable.Status = ChannelStatus.Unavailable;
            unavailable.StatusReason = ChannelStatusReason.Deleted;
            unavailable.StatusUpdatedAt = Clock.UtcNow;
            await Provider.Channels.SaveRefreshResultsAsync(
                new[]
                {
                    new ChannelRefreshResult
                    {
                        Channel = ToDomainChannel(unavailable, Array.Empty<ChannelVideo>()),
                        VideosRefreshed = false
                    }
                },
                CancellationToken.None);

            var stale = await Provider.Channels.GetStaleLookaheadAsync(
                Clock.UtcNow,
                10,
                CancellationToken.None);
            Assert.Equal(active.Id, Assert.Single(stale).Id);
            Assert.Equal(
                active.StaleAfter,
                await Provider.Channels.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None));

            await Provider.Lists.RemoveChannelAsync(list.Id, active.Id);
            Assert.Empty(await Provider.Channels.GetStaleLookaheadAsync(
                Clock.UtcNow,
                10,
                CancellationToken.None));
            Assert.Null(await Provider.Channels.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None));
        }

        protected async Task SubscriptionReferencesAndCountContractAsync()
        {
            var firstList = await CreateListAsync(id: Guid.Parse("10000000-0000-0000-0000-000000000000"));
            var secondList = await CreateListAsync(
                id: Guid.Parse("20000000-0000-0000-0000-000000000000"),
                token: Enumerable.Repeat((byte)2, 40).ToArray());
            var channel = await CreateChannelAsync("subscription-channel");

            await AddChannelToListAsync(secondList.Id, channel.Id);
            await AddChannelToListAsync(firstList.Id, channel.Id);
            await AddChannelToListAsync(firstList.Id, channel.Id);

            var subscribed = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { channel.Id },
                CancellationToken.None));
            Assert.Equal(2, subscribed.SubscriptionCount);
            Assert.Equal(
                new[] { firstList.Id, secondList.Id },
                subscribed.SubscribedListIds.OrderBy(id => id));

            await Provider.Lists.RemoveChannelAsync(firstList.Id, channel.Id);
            var afterRemoval = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { channel.Id },
                CancellationToken.None));
            Assert.Equal(1, afterRemoval.SubscriptionCount);
            Assert.Equal(secondList.Id, Assert.Single(afterRemoval.SubscribedListIds));
        }

        protected async Task ProjectionUpdateContractAsync()
        {
            var list = await CreateListAsync();
            var refreshedModel = await CreateChannelAsync("projected-channel", title: "Before");
            var untouchedModel = await CreateChannelAsync("untouched-channel", title: "Untouched");
            untouchedModel.Url = "https://www.youtube.com/@untouched-channel";
            untouchedModel.Thumbnail = "untouched-distinctive.png";
            untouchedModel.PlaylistId = "untouched-playlist";
            untouchedModel.StaleAfter = Clock.UtcNow.AddHours(2);
            untouchedModel.Status = ChannelStatus.Unavailable;
            untouchedModel.StatusReason = ChannelStatusReason.Deleted;
            untouchedModel.StatusUpdatedAt = Clock.UtcNow.AddMinutes(-10);
            var untouchedVideo = CreateVideo(
                untouchedModel.Id,
                "untouched-video",
                "Untouched Video",
                Clock.UtcNow.AddHours(-2));
            var untouchedDomain = ToDomainChannel(untouchedModel, new[] { untouchedVideo });
            await Provider.Channels.SaveRefreshResultsAsync(
                new[]
                {
                    new ChannelRefreshResult
                    {
                        Channel = untouchedDomain,
                        VideosRefreshed = true,
                        EarliestPublishedAt = untouchedVideo.PublishedAt
                    }
                },
                CancellationToken.None);

            await AddChannelToListAsync(list.Id, refreshedModel.Id);
            await AddChannelToListAsync(list.Id, untouchedModel.Id);

            var refreshed = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { refreshedModel.Id },
                CancellationToken.None));
            Assert.Equal(list.Id, Assert.Single(refreshed.SubscribedListIds));
            refreshed.Url = "https://www.youtube.com/@projected-channel";
            refreshed.Title = "After";
            refreshed.Thumbnail = "after.png";
            refreshed.PlaylistId = "after-playlist";
            refreshed.StaleAfter = Clock.UtcNow.AddHours(1);
            refreshed.Status = ChannelStatus.Unavailable;
            refreshed.StatusReason = ChannelStatusReason.Private;
            refreshed.StatusUpdatedAt = Clock.UtcNow;
            var video = CreateVideo(refreshed.Id, "projected-video", "Projected Video");
            refreshed.Videos = new[] { video };

            await Provider.Channels.SaveRefreshResultsAsync(
                new[]
                {
                    new ChannelRefreshResult
                    {
                        Channel = refreshed,
                        VideosRefreshed = true,
                        EarliestPublishedAt = video.PublishedAt
                    }
                },
                CancellationToken.None);
            await Provider.ListProjections.UpdateProjectedChannelsAsync(
                new[] { refreshed },
                CancellationToken.None);

            var channelProjection = await Provider.Lists.GetChannelProjectionAsync(ToSubscriptionList(list));
            var projectedChannel = channelProjection.Channels.Single(channel => channel.Id == refreshed.Id);
            Assert.Equal(refreshed.Url, projectedChannel.Url);
            Assert.Equal("After", projectedChannel.Title);
            Assert.Equal("after.png", projectedChannel.Thumbnail);
            Assert.Equal(refreshed.StaleAfter, projectedChannel.StaleAfter);
            Assert.Equal(ChannelStatus.Unavailable, projectedChannel.Status);
            Assert.Equal(ChannelStatusReason.Private, projectedChannel.StatusReason);
            Assert.Equal(refreshed.StatusUpdatedAt, projectedChannel.StatusUpdatedAt);
            var untouchedProjectedChannel = channelProjection.Channels.Single(channel => channel.Id == untouchedModel.Id);
            Assert.Equal(untouchedModel.Url, untouchedProjectedChannel.Url);
            Assert.Equal(untouchedModel.Title, untouchedProjectedChannel.Title);
            Assert.Equal(untouchedModel.Thumbnail, untouchedProjectedChannel.Thumbnail);
            Assert.Equal(untouchedModel.StaleAfter, untouchedProjectedChannel.StaleAfter);
            Assert.Equal(untouchedModel.Status, untouchedProjectedChannel.Status);
            Assert.Equal(untouchedModel.StatusReason, untouchedProjectedChannel.StatusReason);
            Assert.Equal(untouchedModel.StatusUpdatedAt, untouchedProjectedChannel.StatusUpdatedAt);

            var videoProjection = await Provider.Lists.GetVideoProjectionAsync(ToSubscriptionList(list), 10);
            var projectedVideoChannel = videoProjection.Channels.Single(channel => channel.Id == refreshed.Id);
            Assert.Equal(refreshed.Url, projectedVideoChannel.Url);
            Assert.Equal(refreshed.Title, projectedVideoChannel.Title);
            Assert.Equal(refreshed.Thumbnail, projectedVideoChannel.Thumbnail);
            Assert.Equal(refreshed.StaleAfter, projectedVideoChannel.StaleAfter);
            Assert.Equal(refreshed.Status, projectedVideoChannel.Status);
            Assert.Equal(refreshed.StatusReason, projectedVideoChannel.StatusReason);
            Assert.Equal(refreshed.StatusUpdatedAt, projectedVideoChannel.StatusUpdatedAt);
            var projectedVideo = Assert.Single(projectedVideoChannel.Videos);
            Assert.Equal(video.VideoId, projectedVideo.VideoId);
            Assert.Equal(video.ChannelId, projectedVideo.ChannelId);
            Assert.Equal(video.Title, projectedVideo.Title);
            Assert.Equal(video.Duration, projectedVideo.Duration);
            Assert.Equal(video.PublishedAt, projectedVideo.PublishedAt);
            Assert.Equal(video.ThumbnailUrl, projectedVideo.ThumbnailUrl);

            var untouchedVideoChannel = videoProjection.Channels.Single(channel => channel.Id == untouchedModel.Id);
            Assert.Equal(untouchedModel.Url, untouchedVideoChannel.Url);
            Assert.Equal(untouchedModel.Title, untouchedVideoChannel.Title);
            Assert.Equal(untouchedModel.Thumbnail, untouchedVideoChannel.Thumbnail);
            Assert.Equal(untouchedModel.StaleAfter, untouchedVideoChannel.StaleAfter);
            Assert.Equal(untouchedModel.Status, untouchedVideoChannel.Status);
            Assert.Equal(untouchedModel.StatusReason, untouchedVideoChannel.StatusReason);
            Assert.Equal(untouchedModel.StatusUpdatedAt, untouchedVideoChannel.StatusUpdatedAt);
            var untouchedProjectedVideo = Assert.Single(untouchedVideoChannel.Videos);
            Assert.Equal(untouchedVideo.VideoId, untouchedProjectedVideo.VideoId);
            Assert.Equal(untouchedVideo.ChannelId, untouchedProjectedVideo.ChannelId);
            Assert.Equal(untouchedVideo.Title, untouchedProjectedVideo.Title);
            Assert.Equal(untouchedVideo.Duration, untouchedProjectedVideo.Duration);
            Assert.Equal(untouchedVideo.PublishedAt, untouchedProjectedVideo.PublishedAt);
            Assert.Equal(untouchedVideo.ThumbnailUrl, untouchedProjectedVideo.ThumbnailUrl);
        }
    }
}
