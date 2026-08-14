using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ChannelRepository _repository;

        public ChannelRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ChannelRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task SaveDiscoveredChannelAsync_DoesNotMatchExistingChannelByUrl()
        {
            var originalStaleAfter = DateTimeOffset.UtcNow.AddHours(2);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-old', @staleAfter);
                ",
                new { staleAfter = originalStaleAfter });

            await _repository.SaveDiscoveredChannelAsync(
                new Channel
                {
                    Id = "channel-2",
                    Url = "https://www.youtube.com/channel/channel-1",
                    Title = "Updated",
                    Thumbnail = "new.png",
                    PlaylistId = "playlist-new"
                },
                DateTimeOffset.MinValue);

            var existing = await QuerySingleAsync<(string Id, string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter)>(
                @"
                SELECT Id, Title, Thumbnail, PlaylistId, StaleAfter
                FROM Channel
                WHERE Id = N'channel-1';
                ");
            var count = await ScalarAsync<int>(
                @"
                SELECT COUNT(*)
                FROM Channel
                WHERE Url = N'https://www.youtube.com/channel/channel-1';
                ");

            Assert.Equal(2, count);
            Assert.Equal("channel-1", existing.Id);
            Assert.Equal("Original", existing.Title);
            Assert.Equal("old.png", existing.Thumbnail);
            Assert.Equal("playlist-old", existing.PlaylistId);
            Assert.Equal(originalStaleAfter, existing.StaleAfter);
        }

        [LocalDbFact]
        public async Task SaveDiscoveredChannelAsync_RediscoversExistingChannelByIdWhenUrlChanged()
        {
            var futureStaleAfter = DateTimeOffset.UtcNow.AddHours(2);
            var rediscoveredStaleAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-old', @staleAfter, @status, @statusReason, @statusUpdatedAt);
                ",
                new
                {
                    staleAfter = futureStaleAfter,
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    statusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                });

            await _repository.SaveDiscoveredChannelAsync(
                new Channel
                {
                    Id = "channel-1",
                    Url = "https://www.youtube.com/user/legacy-name",
                    Title = "Updated",
                    Thumbnail = "new.png",
                    PlaylistId = "playlist-new"
                },
                rediscoveredStaleAfter);

            var count = await ScalarAsync<int>(
                @"
                SELECT COUNT(*)
                FROM Channel
                WHERE Id = N'channel-1';
                ");
            var persisted = await QuerySingleAsync<(string Url, string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                @"
                SELECT Url,
                       Title,
                       Thumbnail,
                       PlaylistId,
                       StaleAfter,
                       Status,
                       StatusReason,
                       StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-1';
                ");

            Assert.Equal(1, count);
            Assert.Equal("https://www.youtube.com/channel/channel-1", persisted.Url);
            Assert.Equal("Original", persisted.Title);
            Assert.Equal("old.png", persisted.Thumbnail);
            Assert.Equal("playlist-old", persisted.PlaylistId);
            Assert.Equal(rediscoveredStaleAfter, persisted.StaleAfter);
            Assert.Equal(ChannelStatus.Active, persisted.Status);
            Assert.Equal(ChannelStatusReason.None, persisted.StatusReason);
            Assert.Null(persisted.StatusUpdatedAt);
        }

        [LocalDbFact]
        public async Task SaveDiscoveredChannelAsync_ConcurrentCallsForSameIdLeaveSingleRow()
        {
            const string url = "https://www.youtube.com/channel/channel-1";
            var staleAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

            var firstSave = _repository.SaveDiscoveredChannelAsync(
                new Channel
                {
                    Id = "channel-1",
                    Url = url,
                    Title = "Original",
                    Thumbnail = "original.png",
                    PlaylistId = "playlist-original"
                },
                staleAfter);

            var secondSave = _repository.SaveDiscoveredChannelAsync(
                new Channel
                {
                    Id = "channel-1",
                    Url = url,
                    Title = "Updated",
                    Thumbnail = "updated.png",
                    PlaylistId = "playlist-updated"
                },
                staleAfter.AddMinutes(1));

            await Task.WhenAll(firstSave, secondSave);

            var persisted = await QuerySingleAsync<(int Count, string Id, string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter)>(
                @"
                SELECT COUNT(*) AS Count,
                       MIN(Id) AS Id,
                       MIN(Title) AS Title,
                       MIN(Thumbnail) AS Thumbnail,
                       MIN(PlaylistId) AS PlaylistId,
                       MAX(StaleAfter) AS StaleAfter
                FROM Channel
                WHERE Url = @url;
                ",
                new { url });

            Assert.Equal(1, persisted.Count);
            Assert.Equal("channel-1", persisted.Id);
            Assert.True(persisted.StaleAfter >= staleAfter);
            Assert.Contains(persisted.Title, new[] { "Original", "Updated" });
            Assert.Contains(persisted.Thumbnail, new[] { "original.png", "updated.png" });
            Assert.Contains(persisted.PlaylistId, new[] { "playlist-original", "playlist-updated" });
        }

        [LocalDbFact]
        public async Task Schema_DefaultsChannelStatusToActive()
        {
            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-default', N'https://www.youtube.com/channel/channel-default', N'Default', N'default.png', N'playlist-default', @staleAfter);
                ",
                new { staleAfter = DateTimeOffset.UtcNow.AddHours(1) });

            var persisted = await QuerySingleAsync<(ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                @"
                SELECT Status, StatusReason, StatusUpdatedAt
                FROM Channel
                WHERE Id = N'channel-default';
                ");

            Assert.Equal(ChannelStatus.Active, persisted.Status);
            Assert.Equal(ChannelStatusReason.None, persisted.StatusReason);
            Assert.Null(persisted.StatusUpdatedAt);
        }

        [LocalDbFact]
        public async Task GetByIdAsync_MapsStatusFields()
        {
            var statusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-15);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-status', N'https://www.youtube.com/channel/channel-status', N'Status', N'status.png', N'playlist-status', @staleAfter, @status, @statusReason, @statusUpdatedAt);
                ",
                new
                {
                    staleAfter = DateTimeOffset.UtcNow.AddHours(1),
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    statusUpdatedAt
                });

            var channel = await _repository.GetByIdAsync("channel-status");

            Assert.NotNull(channel);
            Assert.Equal(ChannelStatus.Unavailable, channel.Status);
            Assert.Equal(ChannelStatusReason.NotFound, channel.StatusReason);
            Assert.Equal(statusUpdatedAt, channel.StatusUpdatedAt);
        }

    }
}
