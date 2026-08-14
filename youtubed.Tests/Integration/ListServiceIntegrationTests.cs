using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ListServiceIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeAppClock _clock;
        private readonly ListService _service;

        public ListServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _clock = new FakeAppClock();
            _service = new ListService(
                new ListRepository(fixture.ConnectionFactory),
                _clock,
                new ChannelRefreshQueue());
        }

        [LocalDbFact]
        public async Task CreateListAsync_PersistsList()
        {
            _clock.UtcNow = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

            var list = await _service.CreateListAsync("Integration List");

            var persisted = await QuerySingleAsync<ListModel>(
                @"
                SELECT Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn
                FROM List
                WHERE Id = @id;
                ",
                new { id = list.Id });

            Assert.Equal("Integration List", list.Title);
            Assert.NotNull(list.Token);
            Assert.Equal(40, list.Token.Length);
            Assert.Equal(1.00m, list.PlaybackRate);
            Assert.Equal(list.Id, persisted.Id);
            Assert.Equal(list.Title, persisted.Title);
            Assert.Equal(1.00m, persisted.PlaybackRate);
            Assert.Equal(list.TokenString, persisted.TokenString);
            Assert.Equal(_clock.UtcNow.Add(Constants.ListMaxAgeMin), persisted.ExpiredAfter);
            Assert.Null(persisted.ExpirationRenewedOn);
        }

        [LocalDbFact]
        public async Task GetListAsync_ReturnsPersistedListAndNullForMissingId()
        {
            var list = await _service.CreateListAsync("Fetch Me");

            var found = await _service.GetListAsync(list.Id);
            var missing = await _service.GetListAsync(Guid.NewGuid());

            Assert.NotNull(found);
            Assert.Equal(list.Id, found.Id);
            Assert.Equal(list.TokenString, found.TokenString);
            Assert.Equal(1.00m, found.PlaybackRate);
            Assert.Null(missing);
        }

        [LocalDbFact]
        public async Task GetListViewAsync_ReturnsOrderedDataWithoutRenewingExpiry()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;
            var originalExpiry = now.AddDays(-1);
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var staleChannelTime = now.AddMinutes(-5);
            var freshChannelTime = now.AddMinutes(5);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, PlaybackRate, ExpiredAfter)
                VALUES (@listId, @token, @title, @playbackRate, @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES
                    (N'channel-b', N'https://www.youtube.com/channel/channel-b', N'Beta', N'beta.png', N'playlist-b', @staleAfter),
                    (N'channel-a', N'https://www.youtube.com/channel/channel-a', N'Alpha', N'alpha.png', N'playlist-a', @freshAfter),
                    (N'channel-g', N'https://www.youtube.com/channel/channel-g', N'Gamma', N'gamma.png', N'playlist-g', @staleAfter);

                UPDATE Channel
                SET Status = @status,
                    StatusReason = @statusReason,
                    StatusUpdatedAt = @now
                WHERE Id = N'channel-g';

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'channel-b'),
                    (@listId, N'channel-a'),
                    (@listId, N'channel-g');

                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES
                    (N'channel-b', N'video-old', N'Oldest', @duration, @oldestPublishedAt, N'old.png'),
                    (N'channel-a', N'video-new', N'Newest', @duration, @newestPublishedAt, N'new.png'),
                    (N'channel-b', N'video-mid', N'Middle', @duration, @middlePublishedAt, N'mid.png');
                ",
                new
                {
                    listId,
                    token,
                    title = "List View",
                    playbackRate = 1.75m,
                    expiredAfter = originalExpiry,
                    staleAfter = staleChannelTime,
                    freshAfter = freshChannelTime,
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    now,
                    duration = TimeSpan.FromMinutes(5).Ticks,
                    newestPublishedAt = now.AddHours(-1),
                    middlePublishedAt = now.AddHours(-2),
                    oldestPublishedAt = now.AddHours(-3)
                });

            var view = await _service.GetListViewAsync(listId);
            var refreshedExpiry = await ScalarAsync<DateTimeOffset>(
                "SELECT ExpiredAfter FROM List WHERE Id = @id;",
                new { id = listId });

            Assert.NotNull(view);
            Assert.Equal("List View", view.Title);
            Assert.Equal(1.75m, view.PlaybackRate);
            Assert.Equal(1, view.StaleCount);
            Assert.Equal("video-new", view.Videos.Select(video => video.VideoId).First());
            Assert.Equal(new[] { "video-new", "video-mid", "video-old" }, view.Videos.Select(video => video.VideoId).ToArray());
            Assert.Equal(TimeSpan.FromMinutes(5), view.Videos.Select(video => video.VideoDuration).First());
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, view.Channels.Select(channel => channel.Title).ToArray());
            Assert.Equal(ChannelStatus.Unavailable, view.Channels.Single(channel => channel.Id == "channel-g").Status);
            Assert.Equal(ChannelStatusReason.NotFound, view.Channels.Single(channel => channel.Id == "channel-g").StatusReason);
            Assert.Equal(originalExpiry, view.ExpiredAfter);
            Assert.Equal(now, view.Now);
            Assert.Equal(originalExpiry.Subtract(now), view.MaxAge);
            Assert.False(view.HasMoreVideos);
            Assert.Equal(originalExpiry, refreshedExpiry);
        }

        [LocalDbFact]
        public async Task GetAuthenticatedListAsync_RenewsExpiryOncePerUtcDay()
        {
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;
            var listId = Guid.NewGuid();
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var originalExpiry = now.AddDays(-1);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Authenticated', @expiredAfter);
                ",
                new
                {
                    listId,
                    token,
                    expiredAfter = originalExpiry
                });

            var tokenString = new ListModel { Token = token }.TokenString;

            var first = await _service.GetAuthenticatedListAsync(listId, tokenString);
            var firstPersisted = await QuerySingleAsync<ListModel>(
                "SELECT Id, Token, Title, ExpiredAfter, ExpirationRenewedOn FROM List WHERE Id = @listId;",
                new { listId });
            _clock.UtcNow = now.AddMinutes(30);
            var second = await _service.GetAuthenticatedListAsync(listId, tokenString);
            var secondPersisted = await QuerySingleAsync<ListModel>(
                "SELECT Id, Token, Title, ExpiredAfter, ExpirationRenewedOn FROM List WHERE Id = @listId;",
                new { listId });
            _clock.UtcNow = now.AddDays(1);
            var third = await _service.GetAuthenticatedListAsync(listId, tokenString);
            var thirdPersisted = await QuerySingleAsync<ListModel>(
                "SELECT Id, Token, Title, ExpiredAfter, ExpirationRenewedOn FROM List WHERE Id = @listId;",
                new { listId });

            Assert.NotNull(first);
            Assert.Equal(originalExpiry, first.ExpiredAfter);
            Assert.Null(first.ExpirationRenewedOn);
            Assert.Equal(now.Add(Constants.ListMaxAgeMin), firstPersisted.ExpiredAfter);
            Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime), firstPersisted.ExpirationRenewedOn);
            Assert.NotNull(second);
            Assert.Equal(firstPersisted.ExpiredAfter, second.ExpiredAfter);
            Assert.Equal(firstPersisted.ExpiredAfter, secondPersisted.ExpiredAfter);
            Assert.NotNull(third);
            Assert.Equal(firstPersisted.ExpiredAfter, third.ExpiredAfter);
            Assert.Equal(now.AddDays(1).Add(Constants.ListMaxAgeMin), thirdPersisted.ExpiredAfter);
            Assert.Equal(DateOnly.FromDateTime(now.UtcDateTime).AddDays(1), thirdPersisted.ExpirationRenewedOn);
        }

        [LocalDbFact]
        public async Task GetAuthenticatedListAsync_InvalidTokenDoesNotRenew()
        {
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;
            var listId = Guid.NewGuid();
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var originalExpiry = now.AddDays(-1);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Authenticated', @expiredAfter);
                ",
                new
                {
                    listId,
                    token,
                    expiredAfter = originalExpiry
                });

            var result = await _service.GetAuthenticatedListAsync(listId, "wrong");
            var persisted = await QuerySingleAsync<ListModel>(
                "SELECT Id, Token, Title, ExpiredAfter, ExpirationRenewedOn FROM List WHERE Id = @listId;",
                new { listId });

            Assert.Null(result);
            Assert.Equal(originalExpiry, persisted.ExpiredAfter);
            Assert.Null(persisted.ExpirationRenewedOn);
        }

        [LocalDbFact]
        public async Task GetListChannelViewAsync_ReturnsChannelsWithoutVideos()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Channel View', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'channel-1');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)5, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-1),
                });

            var view = await _service.GetListChannelViewAsync(listId);

            Assert.NotNull(view);
            Assert.Empty(view.Videos);
            var channel = Assert.Single(view.Channels);
            Assert.Equal("channel-1", channel.Id);
            Assert.Equal(now.AddMinutes(-1), channel.StaleAfter);
            Assert.Equal(1, view.StaleCount);
        }

        [LocalDbFact]
        public async Task AddChannelAsync_IsIdempotent()
        {
            var listId = Guid.NewGuid();

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter);
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)7, 40).ToArray(),
                    expiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                });

            await _service.AddChannelAsync(listId, "channel-1");
            await _service.AddChannelAsync(listId, "channel-1");

            var count = await ScalarAsync<int>(
                "SELECT COUNT(*) FROM ListChannel WHERE ListId = @listId AND ChannelId = N'channel-1';",
                new { listId });

            Assert.Equal(1, count);
        }

        [LocalDbFact]
        public async Task AddChannelAsync_QueuesNewChannel()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;
            var queue = new ChannelRefreshQueue();
            var service = new ListService(
                new ListRepository(Fixture.ConnectionFactory),
                _clock,
                queue);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter);
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)7, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-1),
                });

            await service.AddChannelAsync(listId, "channel-1");

            Assert.Equal("channel-1", Assert.Single(
                await queue.DequeueBatchAsync(10, CancellationToken.None)));
        }

        [LocalDbFact]
        public async Task UpdateRemoveAndDeleteList_UpdatePersistedRows()
        {
            var listId = Guid.NewGuid();

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Original', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'channel-1');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)9, 40).ToArray(),
                    expiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                });

            await _service.UpdateListAsync(listId, "Renamed", 1.25m);
            await _service.RemoveChannelAsync(listId, "channel-1");
            var updatedPlaybackRate = await ScalarAsync<decimal>("SELECT PlaybackRate FROM List WHERE Id = @listId;", new { listId });
            await _service.DeleteListAsync(listId);

            var titleCount = await ScalarAsync<int>("SELECT COUNT(*) FROM List WHERE Id = @listId;", new { listId });
            var mappingCount = await ScalarAsync<int>("SELECT COUNT(*) FROM ListChannel WHERE ListId = @listId;", new { listId });

            Assert.Equal(1.25m, updatedPlaybackRate);
            Assert.Equal(0, titleCount);
            Assert.Equal(0, mappingCount);
        }

    }
}
