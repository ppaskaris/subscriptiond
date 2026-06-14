using System;
using System.Linq;
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
    public sealed class ChannelServiceIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeAppClock _clock;
        private readonly FakeYoutubeService _youtubeService;
        private readonly ChannelService _service;

        public ChannelServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _clock = new FakeAppClock();
            _youtubeService = new FakeYoutubeService();
            _service = new ChannelService(
                new ChannelRepository(fixture.ConnectionFactory),
                _youtubeService,
                _clock,
                new ChannelUrlLookupCache());
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_CachesChannelByUrl()
        {
            const string url = "https://www.youtube.com/channel/channel-1";
            _youtubeService.SetChannel(url, new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Integration Channel",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-1"
            });

            var first = await _service.GetOrCreateChannelAsync(url);
            var second = await _service.GetOrCreateChannelAsync(url);
            var count = await ScalarAsync<int>("SELECT COUNT(*) FROM Channel WHERE Url = @url;", new { url });

            Assert.Equal("channel-1", first.Id);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, _youtubeService.GetChannelCallCount);
            Assert.Equal(1, count);
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_VideoUrlFallbackStoresCanonicalChannelUrl()
        {
            const string videoUrl = "https://www.youtube.com/watch?v=video-1";
            _youtubeService.SetVideoChannel(videoUrl, new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Video Channel",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-2"
            });

            var channel = await _service.GetOrCreateChannelAsync(videoUrl);
            var persistedUrl = await ScalarAsync<string>("SELECT Url FROM Channel WHERE Id = N'channel-2';");

            Assert.Equal("channel-2", channel.Id);
            Assert.Equal("https://www.youtube.com/channel/channel-2", persistedUrl);
            Assert.Equal(1, _youtubeService.GetVideoChannelCallCount);
            Assert.Equal(0, _youtubeService.GetChannelCallCount);
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_VideoFallbackMarksExistingChannelStaleAgain()
        {
            const string videoUrl = "https://www.youtube.com/watch?v=video-2";
            const string canonicalUrl = "https://www.youtube.com/channel/channel-3";
            var futureStaleAfter = DateTimeOffset.UtcNow.AddHours(2);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-3', @url, N'Existing', N'thumb.png', N'playlist-3', @staleAfter, @visibleAfter);
                ",
                new
                {
                    url = canonicalUrl,
                    staleAfter = futureStaleAfter,
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1)
                });

            _youtubeService.SetVideoChannel(videoUrl, new YoutubeChannel
            {
                Id = "channel-3",
                Title = "Existing",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-3"
            });

            await _service.GetOrCreateChannelAsync(videoUrl);

            var staleAfter = await ScalarAsync<DateTimeOffset>("SELECT StaleAfter FROM Channel WHERE Id = N'channel-3';");

            Assert.True(staleAfter <= DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        [LocalDbFact]
        public async Task GetNextStaleChannelOrDefaultAsync_ClaimsOnlyEligibleAttachedChannel()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'eligible-oldest', N'https://www.youtube.com/channel/eligible-oldest', N'Eligible Oldest', N'a.png', N'playlist-a', @oldestStaleAfter, @visibleAfter),
                    (N'eligible-newer', N'https://www.youtube.com/channel/eligible-newer', N'Eligible Newer', N'b.png', N'playlist-b', @newerStaleAfter, @visibleAfter),
                    (N'not-visible', N'https://www.youtube.com/channel/not-visible', N'Not Visible', N'c.png', N'playlist-c', @oldestStaleAfter, @futureVisibleAfter),
                    (N'orphan', N'https://www.youtube.com/channel/orphan', N'Orphan', N'd.png', N'playlist-d', @oldestStaleAfter, @visibleAfter),
                    (N'fresh', N'https://www.youtube.com/channel/fresh', N'Fresh', N'e.png', N'playlist-e', @futureStaleAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'eligible-oldest'),
                    (@listId, N'eligible-newer'),
                    (@listId, N'not-visible'),
                    (@listId, N'fresh');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)4, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    oldestStaleAfter = now.AddMinutes(-10),
                    newerStaleAfter = now.AddMinutes(-5),
                    futureStaleAfter = now.AddMinutes(10),
                    visibleAfter = now.AddMinutes(-1),
                    futureVisibleAfter = now.AddMinutes(10)
                });

            var claimed = await _service.GetNextStaleChannelOrDefaultAsync();
            var visibleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT VisibleAfter FROM Channel WHERE Id = N'eligible-oldest';");

            Assert.NotNull(claimed);
            Assert.Equal("eligible-oldest", claimed.Id);
            Assert.Equal("https://www.youtube.com/channel/eligible-oldest", claimed.Url);
            Assert.Equal("Eligible Oldest", claimed.Title);
            Assert.Equal("a.png", claimed.Thumbnail);
            Assert.Equal("playlist-a", claimed.PlaylistId);
            Assert.Equal(now.Add(Constants.VisibilityTimeoutMin), visibleAfter);
        }

        [LocalDbFact]
        public async Task GetNextStaleChannelOrDefaultAsync_ChannelCanBeRetriedAfterLeaseExpires()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'eligible', N'https://www.youtube.com/channel/eligible', N'Eligible', N'a.png', N'playlist-a', @staleAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'eligible');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)6, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-10),
                    visibleAfter = now.AddMinutes(-1)
                });

            var firstClaim = await _service.GetNextStaleChannelOrDefaultAsync();
            var secondClaim = await _service.GetNextStaleChannelOrDefaultAsync();

            Assert.NotNull(firstClaim);
            Assert.Null(secondClaim);

            await ExecuteAsync(
                @"
                UPDATE Channel
                SET VisibleAfter = @visibleAfter
                WHERE Id = N'eligible';
                ",
                new { visibleAfter = now.AddMinutes(-1) });

            var retryClaim = await _service.GetNextStaleChannelOrDefaultAsync();

            Assert.NotNull(retryClaim);
            Assert.Equal("eligible", retryClaim.Id);
        }

        [LocalDbFact]
        public async Task RefreshMetadataAsync_UpdatesChangedMetadata()
        {
            const string url = "https://www.youtube.com/user/channel-refresh";

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-refresh', @url, N'Original Title', N'old.png', N'playlist-refresh', @staleAfter, @visibleAfter);
                ",
                new
                {
                    url,
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5)
                });

            _youtubeService.SetChannel(url, new YoutubeChannel
            {
                Id = "channel-refresh",
                Title = "Updated Title",
                Thumbnail = "new.png",
                PlaylistId = "playlist-new"
            });

            var refreshed = await _service.RefreshMetadataAsync(new StaleChannelModel
            {
                Id = "channel-refresh",
                Url = url,
                Title = "Original Title",
                Thumbnail = "old.png",
                PlaylistId = "playlist-refresh"
            });

            var persisted = await QuerySingleAsync<(string Url, string Title, string Thumbnail, string PlaylistId, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                @"
                SELECT Url, Title, Thumbnail, PlaylistId, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-refresh';
                ");

            Assert.NotNull(refreshed);
            Assert.Equal("https://www.youtube.com/channel/channel-refresh", refreshed.Url);
            Assert.Equal("playlist-new", refreshed.PlaylistId);
            Assert.Equal("https://www.youtube.com/channel/channel-refresh", persisted.Url);
            Assert.Equal("Updated Title", persisted.Title);
            Assert.Equal("new.png", persisted.Thumbnail);
            Assert.Equal("playlist-new", persisted.PlaylistId);
            Assert.Equal(ChannelStatus.Active, persisted.Status);
            Assert.Equal(ChannelStatusReason.None, persisted.StatusReason);
            Assert.Null(persisted.StatusUpdatedAt);
        }

        [LocalDbFact]
        public async Task RefreshMetadataAsync_UnchangedMetadataSkipsDatabaseUpdate()
        {
            const string url = "https://www.youtube.com/channel/channel-same";
            var staleAfter = DateTimeOffset.UtcNow.AddMinutes(-5);
            var visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-same', @url, N'Same Title', N'same.png', N'playlist-same', @staleAfter, @visibleAfter);
                ",
                new
                {
                    url,
                    staleAfter,
                    visibleAfter
                });

            _youtubeService.SetChannel(url, new YoutubeChannel
            {
                Id = "channel-same",
                Title = "Same Title",
                Thumbnail = "same.png",
                PlaylistId = "playlist-same"
            });

            var refreshed = await _service.RefreshMetadataAsync(new StaleChannelModel
            {
                Id = "channel-same",
                Url = url,
                Title = "Same Title",
                Thumbnail = "same.png",
                PlaylistId = "playlist-same"
            });

            var persisted = await QuerySingleAsync<(string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, DateTimeOffset VisibleAfter)>(
                @"
                SELECT Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter
                FROM Channel
                WHERE Id = N'channel-same';
                ");

            Assert.Equal("Same Title", persisted.Title);
            Assert.Equal("same.png", persisted.Thumbnail);
            Assert.Equal("playlist-same", persisted.PlaylistId);
            Assert.Equal(staleAfter, persisted.StaleAfter);
            Assert.Equal(visibleAfter, persisted.VisibleAfter);
            Assert.NotNull(refreshed);
            Assert.Equal("channel-same", refreshed.Id);
            Assert.Equal(0, _youtubeService.GetChannelCallCount);
            Assert.Equal(1, _youtubeService.GetChannelByIdCallCount);
            Assert.Equal("channel-same", _youtubeService.LastChannelId);
        }

        [LocalDbFact]
        public async Task RefreshMetadataAsync_MissingYoutubeChannelMarksUnavailable()
        {
            const string url = "https://www.youtube.com/channel/channel-missing";
            var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-missing', @url, N'Missing Title', N'missing.png', N'playlist-missing', @staleAfter, @visibleAfter);
                ",
                new
                {
                    url,
                    staleAfter = now.AddMinutes(-5),
                    visibleAfter = now.AddMinutes(-1)
                });

            var refreshed = await _service.RefreshMetadataAsync(new StaleChannelModel
            {
                Id = "channel-missing",
                Url = url,
                Title = "Missing Title",
                Thumbnail = "missing.png",
                PlaylistId = "playlist-missing"
            });

            var persisted = await QuerySingleAsync<(ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt, DateTimeOffset StaleAfter)>(
                @"
                SELECT Status, StatusReason, StatusUpdatedAt, StaleAfter
                FROM Channel
                WHERE Id = N'channel-missing';
                ");

            Assert.Null(refreshed);
            Assert.Equal(ChannelStatus.Unavailable, persisted.Status);
            Assert.Equal(ChannelStatusReason.NotFound, persisted.StatusReason);
            Assert.Equal(now, persisted.StatusUpdatedAt);
            Assert.Equal(now.Add(Constants.ChannelUnavailableStaleDelay), persisted.StaleAfter);
        }

    }
}
