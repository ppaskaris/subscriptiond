using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
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
        private readonly ListService _service;

        public ListServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _service = new ListService(new ListRepository(fixture.ConnectionFactory));
        }

        [LocalDbFact]
        public async Task CreateListAsync_PersistsList()
        {
            var list = await _service.CreateListAsync("Integration List");

            var persisted = await QuerySingleAsync<ListModel>(
                @"
                SELECT Id, Token, Title, ExpiredAfter
                FROM List
                WHERE Id = @id;
                ",
                new { id = list.Id });

            Assert.Equal("Integration List", list.Title);
            Assert.NotNull(list.Token);
            Assert.Equal(40, list.Token.Length);
            Assert.Equal(list.Id, persisted.Id);
            Assert.Equal(list.Title, persisted.Title);
            Assert.Equal(list.TokenString, persisted.TokenString);
            Assert.True(persisted.ExpiredAfter > DateTimeOffset.Now.AddDays(40));
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
            Assert.Null(missing);
        }

        [LocalDbFact]
        public async Task GetListViewAsync_RefreshesExpiryAndReturnsOrderedData()
        {
            var listId = Guid.NewGuid();
            var originalExpiry = DateTimeOffset.UtcNow.AddDays(-1);
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var staleChannelTime = DateTimeOffset.UtcNow.AddMinutes(-5);
            var freshChannelTime = DateTimeOffset.UtcNow.AddMinutes(5);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, @title, @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'channel-b', N'https://www.youtube.com/channel/channel-b', N'Beta', N'beta.png', N'playlist-b', @staleAfter, @visibleAfter),
                    (N'channel-a', N'https://www.youtube.com/channel/channel-a', N'Alpha', N'alpha.png', N'playlist-a', @freshAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'channel-b'),
                    (@listId, N'channel-a');

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
                    expiredAfter = originalExpiry,
                    staleAfter = staleChannelTime,
                    freshAfter = freshChannelTime,
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                    duration = TimeSpan.FromMinutes(5).Ticks,
                    newestPublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    middlePublishedAt = DateTimeOffset.UtcNow.AddHours(-2),
                    oldestPublishedAt = DateTimeOffset.UtcNow.AddHours(-3)
                });

            var view = await _service.GetListViewAsync(listId);
            var refreshedExpiry = await ScalarAsync<DateTimeOffset>(
                "SELECT ExpiredAfter FROM List WHERE Id = @id;",
                new { id = listId });

            Assert.NotNull(view);
            Assert.Equal("List View", view.Title);
            Assert.Equal(1, view.StaleCount);
            Assert.Equal("video-new", view.Videos.Select(video => video.VideoId).First());
            Assert.Equal(new[] { "video-new", "video-mid", "video-old" }, view.Videos.Select(video => video.VideoId).ToArray());
            Assert.Equal(new[] { "Alpha", "Beta" }, view.Channels.Select(channel => channel.Title).ToArray());
            Assert.True(view.ExpiredAfter > originalExpiry);
            Assert.Equal(view.ExpiredAfter, refreshedExpiry);
        }

        [LocalDbFact]
        public async Task AddChannelAsync_IsIdempotent()
        {
            var listId = Guid.NewGuid();

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter, @visibleAfter);
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)7, 40).ToArray(),
                    expiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1)
                });

            await _service.AddChannelAsync(listId, "channel-1");
            await _service.AddChannelAsync(listId, "channel-1");

            var count = await ScalarAsync<int>(
                "SELECT COUNT(*) FROM ListChannel WHERE ListId = @listId AND ChannelId = N'channel-1';",
                new { listId });

            Assert.Equal(1, count);
        }

        [LocalDbFact]
        public async Task RenameRemoveAndDeleteList_UpdatePersistedRows()
        {
            var listId = Guid.NewGuid();

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Original', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'channel-1');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)9, 40).ToArray(),
                    expiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1)
                });

            await _service.RenameListAsync(listId, "Renamed");
            await _service.RemoveChannelAsync(listId, "channel-1");
            await _service.DeleteListAsync(listId);

            var titleCount = await ScalarAsync<int>("SELECT COUNT(*) FROM List WHERE Id = @listId;", new { listId });
            var mappingCount = await ScalarAsync<int>("SELECT COUNT(*) FROM ListChannel WHERE ListId = @listId;", new { listId });

            Assert.Equal(0, titleCount);
            Assert.Equal(0, mappingCount);
        }

        [LocalDbFact]
        public async Task RemoveExpiredListsAsync_DeletesOnlyExpiredRows()
        {
            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES
                    (@expiredId, @expiredToken, N'Expired', @expiredAfter),
                    (@activeId, @activeToken, N'Active', @activeAfter);
                ",
                new
                {
                    expiredId = Guid.NewGuid(),
                    activeId = Guid.NewGuid(),
                    expiredToken = Enumerable.Repeat((byte)1, 40).ToArray(),
                    activeToken = Enumerable.Repeat((byte)2, 40).ToArray(),
                    expiredAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                    activeAfter = DateTimeOffset.UtcNow.AddMinutes(10)
                });

            var removed = await _service.RemoveExpiredListsAsync();
            var remainingTitles = await QueryAsync<string>("SELECT Title FROM List ORDER BY Title;");

            Assert.Equal(1, removed);
            Assert.Equal(new[] { "Active" }, remainingTitles);
        }
    }
}
