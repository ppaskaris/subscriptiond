using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelVideoRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ChannelVideoRepository _repository;

        public ChannelVideoRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ChannelVideoRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task RefreshAsync_WithNoVideosStillUpdatesStaleAfter()
        {
            var beforeRefresh = DateTimeOffset.UtcNow;

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter, @visibleAfter);
                ",
                new
                {
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5)
                });

            await _repository.RefreshAsync(
                "channel-1",
                DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge),
                Array.Empty<ChannelVideoRecord>(),
                DateTimeOffset.UtcNow.AddHours(1));

            var staleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT StaleAfter FROM Channel WHERE Id = N'channel-1';");

            Assert.True(staleAfter > beforeRefresh);
        }

        [LocalDbFact]
        public async Task RefreshAsync_WithVideosUpdatesInsertsAndDeletesExpectedRows()
        {
            var earliestPublishedAt = DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge);
            var staleAfter = DateTimeOffset.UtcNow.AddHours(2);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @channelStaleAfter, @visibleAfter);

                INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                VALUES
                    (N'channel-1', N'video-update', N'Original', @duration, @recentPublishedAt, N'old.png'),
                    (N'channel-1', N'video-delete', N'Old Delete', @duration, @oldPublishedAt, N'delete.png'),
                    (N'channel-1', N'video-keep', N'Recent Keep', @duration, @recentPublishedAt, N'keep.png');
                ",
                new
                {
                    channelStaleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    duration = TimeSpan.FromMinutes(3).Ticks,
                    recentPublishedAt = earliestPublishedAt.AddDays(1),
                    oldPublishedAt = earliestPublishedAt.AddDays(-1)
                });

            await _repository.RefreshAsync(
                "channel-1",
                earliestPublishedAt,
                new[]
                {
                    new ChannelVideoRecord
                    {
                        ChannelId = "channel-1",
                        Id = "video-update",
                        Title = "Updated",
                        Duration = TimeSpan.FromMinutes(8),
                        PublishedAt = earliestPublishedAt.AddHours(1),
                        Thumbnail = "updated.png"
                    },
                    new ChannelVideoRecord
                    {
                        ChannelId = "channel-1",
                        Id = "video-insert",
                        Title = "Inserted",
                        Duration = TimeSpan.FromMinutes(5),
                        PublishedAt = earliestPublishedAt.AddHours(2),
                        Thumbnail = "inserted.png"
                    }
                },
                staleAfter);

            var videos = await QueryAsync<(string Id, string Title, long Duration, string Thumbnail)>(
                @"
                SELECT Id, Title, Duration, Thumbnail
                FROM ChannelVideo
                WHERE ChannelId = N'channel-1'
                ORDER BY Id;
                ");
            var persistedStaleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT StaleAfter FROM Channel WHERE Id = N'channel-1';");

            Assert.Equal(new[] { "video-insert", "video-keep", "video-update" }, videos.Select(video => video.Id).ToArray());
            Assert.Contains(videos, video => video.Id == "video-update" && video.Title == "Updated" && video.Duration == TimeSpan.FromMinutes(8).Ticks && video.Thumbnail == "updated.png");
            Assert.Contains(videos, video => video.Id == "video-insert" && video.Title == "Inserted");
            Assert.Contains(videos, video => video.Id == "video-keep" && video.Title == "Recent Keep");
            Assert.Equal(staleAfter, persistedStaleAfter);
        }
    }
}
