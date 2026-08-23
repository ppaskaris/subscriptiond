using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Moq;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class ListServiceTests
    {
        [Fact]
        public void SubscriptionListTokenDoesNotExposeItsMutableBuffer()
        {
            var source = new byte[] { 1, 2, 3 };
            var list = new SubscriptionList { Token = source };
            source[0] = 9;
            var exposed = list.Token;
            exposed[1] = 9;

            Assert.Equal(new byte[] { 1, 2, 3 }, list.Token);
        }

        [Fact]
        public async Task GetAuthenticatedListViewAsync_ReadsListThenOneBoundedChannelBatch()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var token = Enumerable.Repeat((byte)5, 40).ToArray();
            var list = CreateList(now, token, "UC-a", "UC-b");
            list.ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            lists.Setup(value => value.GetAsync(list.Id)).ReturnsAsync(list);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            channels.Setup(value => value.GetBatchAsync(
                    It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(list.ChannelIds)),
                    CancellationToken.None))
                .ReturnsAsync(new[] { CreateChannel("UC-a", now.AddMinutes(1)) });
            var service = CreateService(lists, channels, now);

            var view = await service.GetAuthenticatedListViewAsync(
                list.Id,
                WebEncoders.Base64UrlEncode(token));

            Assert.Equal(list.Id, view.Id);
            Assert.Contains(view.Channels, channel => channel.Id == "UC-b" && channel.IsMissing);
            lists.Verify(value => value.GetAsync(list.Id), Times.Once);
            channels.Verify(value => value.GetBatchAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                CancellationToken.None), Times.Once);
        }

        [Fact]
        public async Task GetAuthenticatedListViewAsync_RenewsLoadedAggregateBeforeBatchRead()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var token = Enumerable.Repeat((byte)9, 40).ToArray();
            var list = CreateList(now, token);
            list.ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
            var renewedAfter = now.Add(Constants.ListMaxAgeMin);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var sequence = new MockSequence();
            lists.InSequence(sequence).Setup(value => value.GetAsync(list.Id)).ReturnsAsync(list);
            lists.InSequence(sequence).Setup(value => value.RenewExpirationAsync(
                    list,
                    renewedAfter,
                    DateOnly.FromDateTime(now.UtcDateTime)))
                .ReturnsAsync(() =>
                {
                    list.ExpiredAfter = renewedAfter;
                    list.ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime);
                    return list;
                });
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            channels.Setup(value => value.GetBatchAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    CancellationToken.None))
                .ReturnsAsync(Array.Empty<Channel>());
            var service = CreateService(lists, channels, now);

            var view = await service.GetAuthenticatedListViewAsync(
                list.Id,
                WebEncoders.Base64UrlEncode(token));

            Assert.Equal(renewedAfter, view.ExpiredAfter);
            Assert.Equal(Constants.ListMaxAgeMin, view.MaxAge);
            lists.Verify(value => value.GetAsync(list.Id), Times.Once);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not-base64!")]
        public async Task GetAuthenticatedListViewAsync_InvalidTokenDoesNotReadPersistence(string token)
        {
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            var service = CreateService(lists, channels, DateTimeOffset.UtcNow);

            Assert.Null(await service.GetAuthenticatedListViewAsync(Guid.NewGuid(), token));
        }

        [Fact]
        public async Task GetAuthenticatedListAsync_TokenMismatchDoesNotRenewOrReadChannels()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var list = CreateList(now, Enumerable.Repeat((byte)1, 40).ToArray());
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            lists.Setup(value => value.GetAsync(list.Id)).ReturnsAsync(list);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            var service = CreateService(lists, channels, now);

            Assert.Null(await service.GetAuthenticatedListAsync(
                list.Id,
                WebEncoders.Base64UrlEncode(Enumerable.Repeat((byte)2, 40).ToArray())));
        }

        [Fact]
        public async Task GetListViewAsync_ComposesVideosGloballyWithStableTieBreakAndCap()
        {
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray(),
                Title = "Composed List",
                PlaybackRate = 1.5m,
                ExpiredAfter = now.Add(Constants.ListMaxAgeMin),
                ChannelIds = new[] { "UC-b", "UC-a" }
            };
            var first = CreateChannel("UC-a", now.AddMinutes(1));
            first.Title = "First";
            first.Videos = CreateVideos(first.Id, "a", now, Constants.ListRenderMaxItems).ToArray();
            var second = CreateChannel("UC-b", now.AddMinutes(1));
            second.Title = "Second";
            second.Videos = new[]
            {
                CreateVideo(second.Id, "video-b", now),
                CreateVideo(second.Id, "video-a", now)
            };
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            channels.Setup(value => value.GetBatchAsync(
                    It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(list.ChannelIds)),
                    CancellationToken.None))
                .ReturnsAsync(new[] { second, first });
            var service = CreateService(lists, channels, now);

            var view = await service.GetListViewAsync(list);

            Assert.Equal(Constants.ListRenderMaxItems, view.Videos.Count());
            Assert.True(view.HasMoreVideos);
            Assert.Equal(new[] { "video-a", "video-b" }, view.Videos.Take(2).Select(video => video.VideoId));
            Assert.All(view.Videos.Take(2), video => Assert.Equal("Second", video.ChannelTitle));
            Assert.Equal(WebEncoders.Base64UrlEncode(list.Token), view.Token);
            lists.Verify(value => value.GetAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task GetListChannelViewAsync_MapsMissingAndQueuesMissingAndActiveStaleChannels()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = Array.Empty<byte>(),
                ExpiredAfter = now.AddDays(1),
                ChannelIds = new[] { "missing", "stale", "fresh", "unavailable" }
            };
            var persisted = new[]
            {
                CreateChannel("stale", now.AddMinutes(-1)),
                CreateChannel("fresh", now.AddMinutes(1)),
                CreateChannel("unavailable", now.AddMinutes(-1), ChannelStatus.Unavailable)
            };
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            channels.Setup(value => value.GetBatchAsync(
                    It.IsAny<IReadOnlyCollection<string>>(),
                    CancellationToken.None))
                .ReturnsAsync(persisted);
            var queue = new Mock<IChannelRefreshQueue>(MockBehavior.Strict);
            queue.Setup(value => value.Enqueue(It.IsAny<IReadOnlyCollection<ChannelRefreshRequest>>()))
                .Returns(2);
            var service = new ListService(lists.Object, channels.Object, new FakeAppClock { UtcNow = now }, queue.Object);

            var view = await service.GetListChannelViewAsync(list);

            Assert.Empty(view.Videos);
            var missing = view.Channels.Single(channel => channel.Id == "missing");
            Assert.True(missing.IsMissing);
            Assert.Equal("Temporarily unavailable", missing.Title);
            Assert.Equal("https://www.youtube.com/channel/missing", missing.Url);
            queue.Verify(value => value.Enqueue(It.Is<IReadOnlyCollection<ChannelRefreshRequest>>(requests =>
                requests.Count == 2
                && requests.Any(request => request.ChannelId == "missing" && request.Reason == ChannelRefreshReason.Missing)
                && requests.Any(request => request.ChannelId == "stale"
                    && request.Reason == ChannelRefreshReason.Stale
                    && request.StaleAfter == now.AddMinutes(-1)))), Times.Once);
        }

        [Fact]
        public async Task ForceRefreshAsync_UsesLoadedMembershipWithoutPersistenceReads()
        {
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                ChannelIds = new[] { "channel-1", "channel-2" }
            };
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            var queue = new Mock<IChannelRefreshQueue>(MockBehavior.Strict);
            queue.Setup(value => value.Enqueue(It.IsAny<IReadOnlyCollection<ChannelRefreshRequest>>()))
                .Returns(2);
            var service = new ListService(lists.Object, channels.Object, new FakeAppClock(), queue.Object);

            await service.ForceRefreshAsync(list);

            queue.Verify(value => value.Enqueue(It.Is<IReadOnlyCollection<ChannelRefreshRequest>>(requests =>
                requests.Select(request => request.ChannelId).SequenceEqual(list.ChannelIds)
                && requests.All(request => request.Reason == ChannelRefreshReason.Forced))), Times.Once);
        }

        [Fact]
        public async Task AddChannelAsync_PersistsMembershipThenQueuesDiscovery()
        {
            var id = Guid.NewGuid();
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            lists.Setup(value => value.AddChannelAsync(id, "UC-new")).Returns(Task.CompletedTask);
            var channels = new Mock<IChannelRepository>(MockBehavior.Strict);
            var queue = new Mock<IChannelRefreshQueue>(MockBehavior.Strict);
            queue.Setup(value => value.TryEnqueue(It.IsAny<ChannelRefreshRequest>())).Returns(true);
            var service = new ListService(lists.Object, channels.Object, new FakeAppClock(), queue.Object);

            await service.AddChannelAsync(id, "UC-new");

            queue.Verify(value => value.TryEnqueue(It.Is<ChannelRefreshRequest>(request =>
                request.ChannelId == "UC-new" && request.Reason == ChannelRefreshReason.Missing)), Times.Once);
        }

        private static ListService CreateService(
            Mock<IListRepository> lists,
            Mock<IChannelRepository> channels,
            DateTimeOffset now)
        {
            return new ListService(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now },
                new ChannelRefreshQueue());
        }

        private static SubscriptionList CreateList(
            DateTimeOffset now,
            byte[] token,
            params string[] channelIds)
        {
            return new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = token,
                Title = "List",
                PlaybackRate = 1.25m,
                ExpiredAfter = now.AddDays(45),
                ChannelIds = channelIds
            };
        }

        private static Channel CreateChannel(
            string id,
            DateTimeOffset staleAfter,
            ChannelStatus status = ChannelStatus.Active)
        {
            return new Channel
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = id,
                Thumbnail = $"{id}.png",
                PlaylistId = id,
                StaleAfter = staleAfter,
                Status = status
            };
        }

        private static IEnumerable<ChannelVideo> CreateVideos(
            string channelId,
            string prefix,
            DateTimeOffset now,
            int count)
        {
            return Enumerable.Range(0, count).Select(index =>
                CreateVideo(channelId, $"{prefix}-{index:D3}", now.AddMinutes(-1 - index)));
        }

        private static ChannelVideo CreateVideo(
            string channelId,
            string id,
            DateTimeOffset publishedAt)
        {
            return new ChannelVideo
            {
                ChannelId = channelId,
                VideoId = id,
                Title = id,
                Duration = TimeSpan.FromMinutes(5),
                PublishedAt = publishedAt,
                ThumbnailUrl = $"{id}.png"
            };
        }
    }
}
