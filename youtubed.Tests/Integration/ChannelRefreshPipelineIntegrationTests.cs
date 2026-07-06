using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        public async Task RefreshStaleChannelsAsync_PersistsMetadataVideosAndNextStaleTime()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            await ExecuteAsync(
                @"
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
                    staleAfter = now.AddMinutes(-5),
                });

            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "new.png",
                PlaylistId = "playlist-new"
            });
            youtube.SetVideos(
                "playlist-new",
                new YoutubeVideo
                {
                    ChannelId = "channel-1",
                    Id = "video-1",
                    Title = "Video",
                    Duration = TimeSpan.FromMinutes(7),
                    PublishedAt = now.AddMinutes(-10),
                    Thumbnail = "video.png"
                });
            var pipeline = new ChannelRefreshPipeline(
                new ChannelRepository(Fixture.ConnectionFactory),
                youtube,
                new SqlListProjectionRepository(),
                new FakeAppClock
                {
                    UtcNow = now,
                    RandomDelayValue = TimeSpan.FromMinutes(60)
                },
                new ImmediateYoutubeCallDelay());

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            var channel = await QuerySingleAsync<(string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                @"
                SELECT Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-1';
                ");
            var video = await QuerySingleAsync<(string Id, string Title, long Duration, DateTimeOffset PublishedAt, string Thumbnail)>(
                @"
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
        public async Task RefreshStaleChannelsAsync_MetadataWithoutPlaylistPersistsEmptyPlaylistAndNextStaleTime()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            await ExecuteAsync(
                @"
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
                    staleAfter = now.AddMinutes(-5),
                });

            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "new.png"
            });
            var pipeline = new ChannelRefreshPipeline(
                new ChannelRepository(Fixture.ConnectionFactory),
                youtube,
                new SqlListProjectionRepository(),
                new FakeAppClock
                {
                    UtcNow = now,
                    RandomDelayValue = TimeSpan.FromMinutes(60)
                },
                new ImmediateYoutubeCallDelay());

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            var channel = await QuerySingleAsync<(string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                @"
                SELECT Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-1';
                ");

            Assert.Equal(0, result.RefreshedChannelCount);
            Assert.Equal("Updated", channel.Title);
            Assert.Equal("new.png", channel.Thumbnail);
            Assert.Equal(string.Empty, channel.PlaylistId);
            Assert.Equal(now.AddMinutes(60), channel.StaleAfter);
            Assert.Equal(ChannelStatus.Active, channel.Status);
            Assert.Equal(ChannelStatusReason.None, channel.StatusReason);
            Assert.Null(channel.StatusUpdatedAt);
        }

        private sealed class ImmediateYoutubeCallDelay : IYoutubeCallDelay
        {
            public Task DelayAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
