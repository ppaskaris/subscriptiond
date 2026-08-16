using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelRefreshPipelineIntegrationTests : LocalDbIntegrationTestBase
    {
        public ChannelRefreshPipelineIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
        }

        [LocalDbFact]
        public async Task RefreshAsync_PersistsMetadataVideosAndNextStaleTime()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            await InsertChannelAsync(now);
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "new.png",
                PlaylistId = "playlist-new"
            });
            youtube.SetVideos("playlist-new", new YoutubeVideo
            {
                ChannelId = "channel-1",
                Id = "video-1",
                Title = "Video",
                Duration = TimeSpan.FromMinutes(7),
                PublishedAt = now.AddMinutes(-10),
                Thumbnail = "video.png"
            });
            var pipeline = CreatePipeline(youtube, now);

            var result = await pipeline.RefreshAsync(
                new[] { new ChannelRefreshRequest("channel-1", ChannelRefreshReason.Stale, now) },
                CancellationToken.None);

            var channel = await QuerySingleAsync<(string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(@"
                SELECT Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-1';
                ");
            var video = await QuerySingleAsync<(string Id, string Title, long Duration, DateTimeOffset PublishedAt, string Thumbnail)>(@"
                SELECT Id, Title, Duration, PublishedAt, Thumbnail
                FROM ChannelVideo
                WHERE ChannelId = N'channel-1';
                ");

            Assert.Equal(1, result.RefreshedChannelCount);
            Assert.Equal("Updated", channel.Title);
            Assert.Equal("new.png", channel.Thumbnail);
            Assert.Equal("playlist-new", channel.PlaylistId);
            Assert.Equal(now.AddMinutes(60), channel.StaleAfter);
            Assert.Equal(ChannelStatus.Active, channel.Status);
            Assert.Equal(ChannelStatusReason.None, channel.StatusReason);
            Assert.Null(channel.StatusUpdatedAt);
            Assert.Equal("video-1", video.Id);
            Assert.Equal("Video", video.Title);
            Assert.Equal(TimeSpan.FromMinutes(7).Ticks, video.Duration);
            Assert.Equal(now.AddMinutes(-10), video.PublishedAt);
            Assert.Equal("video.png", video.Thumbnail);
        }

        [LocalDbFact]
        public async Task RefreshAsync_MetadataWithoutPlaylistPersistsEmptyPlaylistAndNextStaleTime()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            await InsertChannelAsync(now);
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "new.png"
            });

            var result = await CreatePipeline(youtube, now)
                .RefreshAsync(
                    new[] { new ChannelRefreshRequest("channel-1", ChannelRefreshReason.Stale, now) },
                    CancellationToken.None);

            var channel = await QuerySingleAsync<(string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(@"
                SELECT Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-1';
                ");
            Assert.Equal(1, result.RefreshedChannelCount);
            Assert.Equal("Updated", channel.Title);
            Assert.Equal("new.png", channel.Thumbnail);
            Assert.Equal(string.Empty, channel.PlaylistId);
            Assert.Equal(now.AddMinutes(60), channel.StaleAfter);
            Assert.Equal(ChannelStatus.Active, channel.Status);
            Assert.Equal(ChannelStatusReason.None, channel.StatusReason);
            Assert.Null(channel.StatusUpdatedAt);
        }

        [LocalDbFact]
        public async Task RefreshAsync_MissingCacheIsCreatedAndUnavailableCacheIsNegativeCached()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-created", new YoutubeChannel
            {
                Id = "channel-created",
                Title = "Created",
                Thumbnail = "created.png"
            });
            var pipeline = CreatePipeline(youtube, now);

            var result = await pipeline.RefreshAsync(
                new[]
                {
                    new ChannelRefreshRequest("channel-created", ChannelRefreshReason.Missing),
                    new ChannelRefreshRequest("channel-gone", ChannelRefreshReason.Missing)
                },
                CancellationToken.None);

            var rows = await QueryAsync<(string Id, string Title, string Thumbnail, ChannelStatus Status)>(@"
                SELECT Id, Title, Thumbnail, Status
                FROM Channel
                WHERE Id IN (N'channel-created', N'channel-gone')
                ORDER BY Id;
                ");
            Assert.Equal(2, rows.Count);
            Assert.Equal(("channel-created", "Created", "created.png", ChannelStatus.Active), rows[0]);
            Assert.Equal(("channel-gone", string.Empty, string.Empty, ChannelStatus.Unavailable), rows[1]);
            Assert.Equal(1, result.RefreshedChannelCount);
            Assert.Equal(1, result.UnavailableChannelCount);
        }

        private async Task InsertChannelAsync(DateTimeOffset now)
        {
            var listId = Guid.NewGuid();
            await ExecuteAsync(@"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-old', @staleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'channel-1');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)6, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-5)
                });
        }

        private ChannelRefreshPipeline CreatePipeline(FakeYoutubeService youtube, DateTimeOffset now)
        {
            return new ChannelRefreshPipeline(
                new ChannelRepository(Fixture.ConnectionFactory),
                youtube,
                new FakeAppClock { UtcNow = now, RandomDelayValue = TimeSpan.FromMinutes(60) },
                Options.Create(new YoutubeSyncOptions()),
                NullLogger<ChannelRefreshPipeline>.Instance);
        }
    }
}
