using System;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Domain;
using Xunit;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ListRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ListRepository _repository;

        public ListRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ListRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task ProjectionReads_ReturnEmptyChannelsWhenListHasNoChannels()
        {
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = Enumerable.Repeat((byte)1, 40).ToArray(),
                Title = "No Channels",
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1)
            };

            var videoProjection = await _repository.GetVideoProjectionAsync(
                list,
                Constants.ListRenderMaxItems + 1);
            var channelProjection = await _repository.GetChannelProjectionAsync(
                list);

            Assert.Empty(videoProjection.Channels);
            Assert.Empty(channelProjection.Channels);
        }

        [LocalDbFact]
        public async Task GetVideoProjectionAsync_ReturnsChannelsAndLimitedVideos()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Projection List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'channel-b', N'https://www.youtube.com/channel/channel-b', N'Beta', N'beta.png', N'playlist-b', @staleAfter, @visibleAfter),
                    (N'channel-a', N'https://www.youtube.com/channel/channel-a', N'Alpha', N'alpha.png', N'playlist-a', @staleAfter, @visibleAfter),
                    (N'channel-empty', N'https://www.youtube.com/channel/channel-empty', N'Empty', N'empty.png', N'playlist-empty', @staleAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'channel-b'),
                    (@listId, N'channel-a'),
                    (@listId, N'channel-empty');

                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES
                    (N'channel-b', N'video-old', N'Oldest', @duration, @oldestPublishedAt, N'old.png'),
                    (N'channel-a', N'video-b', N'Newest B', @duration, @newestPublishedAt, N'b.png'),
                    (N'channel-a', N'video-a', N'Newest A', @duration, @newestPublishedAt, N'a.png');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)1, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-5),
                    visibleAfter = now.AddMinutes(-1),
                    duration = TimeSpan.FromMinutes(5).Ticks,
                    newestPublishedAt = now.AddMinutes(-1),
                    oldestPublishedAt = now.AddMinutes(-2)
                });

            var list = new SubscriptionList
            {
                Id = listId,
                Token = Enumerable.Repeat((byte)1, 40).ToArray(),
                Title = "Projection List",
                ExpiredAfter = now.AddDays(1)
            };

            var projection = await _repository.GetVideoProjectionAsync(list, videoLimit: 2);

            Assert.NotNull(projection);
            Assert.Equal(now.AddDays(1), projection.List.ExpiredAfter);
            Assert.Null(projection.List.ExpirationRenewedOn);
            Assert.Equal(new[] { "Alpha", "Beta", "Empty" }, projection.Channels.Select(channel => channel.Title).ToArray());
            Assert.Empty(projection.Channels.Single(channel => channel.Id == "channel-empty").Videos);
            Assert.Equal(new[] { "video-a", "video-b" }, projection.Channels.SelectMany(channel => channel.Videos).Select(video => video.VideoId).ToArray());
        }

        [LocalDbFact]
        public async Task GetChannelProjectionAsync_DoesNotRequireVideoRows()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Channels', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter, @visibleAfter, @status, @statusReason, @now);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'channel-1');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)2, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-5),
                    visibleAfter = now.AddMinutes(-1),
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    now
                });

            var list = new SubscriptionList
            {
                Id = listId,
                Token = Enumerable.Repeat((byte)2, 40).ToArray(),
                Title = "Channels",
                ExpiredAfter = now.AddDays(1)
            };

            var projection = await _repository.GetChannelProjectionAsync(list);

            var channel = Assert.Single(projection.Channels);
            Assert.Equal("channel-1", channel.Id);
            Assert.Equal(ChannelStatus.Unavailable, channel.Status);
            Assert.Equal(ChannelStatusReason.NotFound, channel.StatusReason);
            Assert.Equal(now.AddDays(1), projection.List.ExpiredAfter);
            Assert.Null(projection.List.ExpirationRenewedOn);
        }

        [LocalDbFact]
        public async Task RenewExpirationAsync_RenewsAtMostOncePerUtcDay()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var originalExpiry = now.AddDays(-1);
            var firstRenewedExpiry = now.AddDays(45);
            var secondRenewedExpiry = now.AddDays(46);
            var nextDayExpiry = now.AddDays(47);
            var renewedOn = DateOnly.FromDateTime(now.UtcDateTime);
            var nextRenewedOn = renewedOn.AddDays(1);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Renew Me', @expiredAfter);
                ",
                new
                {
                    listId,
                    token,
                    expiredAfter = originalExpiry
                });

            await _repository.RenewExpirationAsync(listId, firstRenewedExpiry, renewedOn);
            var first = await _repository.GetAsync(listId);
            await _repository.RenewExpirationAsync(listId, secondRenewedExpiry, renewedOn);
            var second = await _repository.GetAsync(listId);
            await _repository.RenewExpirationAsync(listId, nextDayExpiry, nextRenewedOn);
            var third = await _repository.GetAsync(listId);

            Assert.NotNull(first);
            Assert.Equal(firstRenewedExpiry, first.ExpiredAfter);
            Assert.Equal(renewedOn, first.ExpirationRenewedOn);
            Assert.NotNull(second);
            Assert.Equal(firstRenewedExpiry, second.ExpiredAfter);
            Assert.Equal(renewedOn, second.ExpirationRenewedOn);
            Assert.NotNull(third);
            Assert.Equal(nextDayExpiry, third.ExpiredAfter);
            Assert.Equal(nextRenewedOn, third.ExpirationRenewedOn);
        }

        [LocalDbFact]
        public async Task CreateGetAndUpdateAsync_PersistPlaybackRate()
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = new byte[40],
                Title = "Playback List",
                PlaybackRate = 1.50m,
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1)
            };

            await _repository.CreateAsync(list);
            var created = await _repository.GetAsync(list.Id);
            await _repository.UpdateAsync(list.Id, "Updated Playback List", 2.00m);
            var updated = await _repository.GetAsync(list.Id);

            Assert.Equal(1.50m, created.PlaybackRate);
            Assert.Equal("Updated Playback List", updated.Title);
            Assert.Equal(2.00m, updated.PlaybackRate);
        }
    }
}
