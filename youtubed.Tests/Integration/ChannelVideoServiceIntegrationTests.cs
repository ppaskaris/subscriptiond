using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Models;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelVideoServiceIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeYoutubeService _youtubeService;
        private readonly ChannelVideoService _service;

        public ChannelVideoServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _youtubeService = new FakeYoutubeService();
            _service = new ChannelVideoService(fixture.ConnectionFactory, _youtubeService);
        }

        [LocalDbFact]
        public async Task RefreshVideosAsync_InsertsTargetVideosAndAdvancesStaleAfter()
        {
            var beforeRefresh = DateTimeOffset.UtcNow;

            await SeedChannelAsync("channel-1", "playlist-1");
            _youtubeService.SetVideos(
                "playlist-1",
                new YoutubeVideo
                {
                    Id = "video-1",
                    ChannelId = "channel-1",
                    Title = "Newest",
                    Duration = TimeSpan.FromMinutes(6),
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    Thumbnail = "newest.png"
                },
                new YoutubeVideo
                {
                    Id = "video-2",
                    ChannelId = "channel-1",
                    Title = "Older",
                    Duration = TimeSpan.FromMinutes(4),
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-2),
                    Thumbnail = "older.png"
                },
                new YoutubeVideo
                {
                    Id = "video-foreign",
                    ChannelId = "other-channel",
                    Title = "Other Channel",
                    Duration = TimeSpan.FromMinutes(5),
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-3),
                    Thumbnail = "other.png"
                });

            await _service.RefreshVideosAsync(new StaleChannelModel
            {
                Id = "channel-1",
                PlaylistId = "playlist-1"
            });

            var storedIds = await QueryAsync<string>(
                "SELECT Id FROM ChannelVideo WHERE ChannelId = N'channel-1' ORDER BY PublishedAt DESC, Id ASC;");
            var staleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT StaleAfter FROM Channel WHERE Id = N'channel-1';");

            Assert.Equal(new[] { "video-1", "video-2" }, storedIds);
            Assert.True(staleAfter > beforeRefresh);
            Assert.Equal(1, _youtubeService.GetVideosCallCount);
            Assert.True(_youtubeService.LastPublishedAfter <= DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge).AddMinutes(1));
        }

        [LocalDbFact]
        public async Task RefreshVideosAsync_UpdatesExistingVideos()
        {
            await SeedChannelAsync("channel-2", "playlist-2");
            await ExecuteAsync(
                @"
                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES (N'channel-2', N'video-1', N'Original', @duration, @publishedAt, N'old.png');
                ",
                new
                {
                    duration = TimeSpan.FromMinutes(1).Ticks,
                    publishedAt = DateTimeOffset.UtcNow.AddHours(-4)
                });

            _youtubeService.SetVideos(
                "playlist-2",
                new YoutubeVideo
                {
                    Id = "video-1",
                    ChannelId = "channel-2",
                    Title = "Updated",
                    Duration = TimeSpan.FromMinutes(9),
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    Thumbnail = "updated.png"
                });

            await _service.RefreshVideosAsync(new StaleChannelModel
            {
                Id = "channel-2",
                PlaylistId = "playlist-2"
            });

            var persisted = await QuerySingleAsync<(string Title, long Duration, string Thumbnail)>(
                @"
                SELECT Title, Duration, Thumbnail
                FROM ChannelVideo
                WHERE ChannelId = N'channel-2' AND Id = N'video-1';
                ");

            Assert.Equal("Updated", persisted.Title);
            Assert.Equal(TimeSpan.FromMinutes(9).Ticks, persisted.Duration);
            Assert.Equal("updated.png", persisted.Thumbnail);
        }

        [LocalDbFact]
        public async Task RefreshVideosAsync_EmptyResultUsesDeleteBranchAndKeepsRecentVideos()
        {
            var beforeRefresh = DateTimeOffset.UtcNow;

            await SeedChannelAsync("channel-3", "playlist-3");
            await ExecuteAsync(
                @"
                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES
                    (N'channel-3', N'video-old', N'Old', @duration, @oldPublishedAt, N'old.png'),
                    (N'channel-3', N'video-recent', N'Recent', @duration, @recentPublishedAt, N'recent.png');
                ",
                new
                {
                    duration = TimeSpan.FromMinutes(3).Ticks,
                    oldPublishedAt = DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge).AddDays(-2),
                    recentPublishedAt = DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge).AddDays(2)
                });

            _youtubeService.SetVideos("playlist-3");

            await _service.RefreshVideosAsync(new StaleChannelModel
            {
                Id = "channel-3",
                PlaylistId = "playlist-3"
            });

            var remaining = await QueryAsync<string>(
                "SELECT Id FROM ChannelVideo WHERE ChannelId = N'channel-3' ORDER BY Id;");
            var staleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT StaleAfter FROM Channel WHERE Id = N'channel-3';");

            Assert.Equal(new[] { "video-recent" }, remaining);
            Assert.True(staleAfter > beforeRefresh);
        }

        [LocalDbFact]
        public async Task RefreshVideosAsync_DeletesOnlyOldUnmatchedVideosInTvpBranch()
        {
            await SeedChannelAsync("channel-4", "playlist-4");
            await ExecuteAsync(
                @"
                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES
                    (N'channel-4', N'video-old', N'Old', @duration, @oldPublishedAt, N'old.png'),
                    (N'channel-4', N'video-recent', N'Recent', @duration, @recentPublishedAt, N'recent.png');
                ",
                new
                {
                    duration = TimeSpan.FromMinutes(2).Ticks,
                    oldPublishedAt = DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge).AddDays(-1),
                    recentPublishedAt = DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge).AddDays(1)
                });

            _youtubeService.SetVideos(
                "playlist-4",
                new YoutubeVideo
                {
                    Id = "video-new",
                    ChannelId = "channel-4",
                    Title = "New",
                    Duration = TimeSpan.FromMinutes(7),
                    PublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    Thumbnail = "new.png"
                });

            await _service.RefreshVideosAsync(new StaleChannelModel
            {
                Id = "channel-4",
                PlaylistId = "playlist-4"
            });

            var remaining = await QueryAsync<string>(
                "SELECT Id FROM ChannelVideo WHERE ChannelId = N'channel-4' ORDER BY Id;");

            Assert.Equal(new[] { "video-new", "video-recent" }, remaining);
        }

        private Task SeedChannelAsync(string channelId, string playlistId)
        {
            return ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (@channelId, @url, @title, N'thumb.png', @playlistId, @staleAfter, @visibleAfter);
                ",
                new
                {
                    channelId,
                    url = $"https://www.youtube.com/channel/{channelId}",
                    title = channelId,
                    playlistId,
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5)
                });
        }
    }
}
