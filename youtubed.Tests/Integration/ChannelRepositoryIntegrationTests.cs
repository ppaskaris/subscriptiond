using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
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
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-old', @staleAfter, @visibleAfter);
                ",
                new
                {
                    staleAfter = originalStaleAfter,
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1)
                });

            await _repository.SaveDiscoveredChannelAsync(
                new ChannelModel
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
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-old', @staleAfter, @visibleAfter, @status, @statusReason, @statusUpdatedAt);
                ",
                new
                {
                    staleAfter = futureStaleAfter,
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-1),
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    statusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                });

            await _repository.SaveDiscoveredChannelAsync(
                new ChannelModel
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
                new ChannelModel
                {
                    Id = "channel-1",
                    Url = url,
                    Title = "Original",
                    Thumbnail = "original.png",
                    PlaylistId = "playlist-original"
                },
                staleAfter);

            var secondSave = _repository.SaveDiscoveredChannelAsync(
                new ChannelModel
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
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-default', N'https://www.youtube.com/channel/channel-default', N'Default', N'default.png', N'playlist-default', @staleAfter, @visibleAfter);
                ",
                new
                {
                    staleAfter = DateTimeOffset.UtcNow.AddHours(1),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(30)
                });

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
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-status', N'https://www.youtube.com/channel/channel-status', N'Status', N'status.png', N'playlist-status', @staleAfter, @visibleAfter, @status, @statusReason, @statusUpdatedAt);
                ",
                new
                {
                    staleAfter = DateTimeOffset.UtcNow.AddHours(1),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(30),
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

        [LocalDbFact]
        public async Task UpdateMetadataAsync_UpdatesMetadataAndClearsUnavailableStatus()
        {
            var staleAfter = DateTimeOffset.UtcNow.AddHours(1);
            var visibleAfter = DateTimeOffset.UtcNow.AddMinutes(30);
            var statusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-15);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                VALUES (N'channel-1', N'https://www.youtube.com/user/channel-1', N'Original', N'old.png', N'playlist-1', @staleAfter, @visibleAfter, @status, @statusReason, @statusUpdatedAt);
                ",
                new
                {
                    staleAfter,
                    visibleAfter,
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    statusUpdatedAt
                });

            await _repository.UpdateMetadataAsync(
                "channel-1",
                "https://www.youtube.com/channel/channel-1",
                "Updated",
                "new.png",
                "playlist-new");

            var persisted = await QuerySingleAsync<(string Url, string Title, string Thumbnail, string PlaylistId, ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt, DateTimeOffset StaleAfter, DateTimeOffset VisibleAfter)>(
                @"
                SELECT Url, Title, Thumbnail, PlaylistId, Status, StatusReason, StatusUpdatedAt, StaleAfter, VisibleAfter
                FROM Channel
                WHERE Id = N'channel-1';
                ");

            Assert.Equal("https://www.youtube.com/channel/channel-1", persisted.Url);
            Assert.Equal("Updated", persisted.Title);
            Assert.Equal("new.png", persisted.Thumbnail);
            Assert.Equal("playlist-new", persisted.PlaylistId);
            Assert.Equal(ChannelStatus.Active, persisted.Status);
            Assert.Equal(ChannelStatusReason.None, persisted.StatusReason);
            Assert.Null(persisted.StatusUpdatedAt);
            Assert.Equal(staleAfter, persisted.StaleAfter);
            Assert.Equal(visibleAfter, persisted.VisibleAfter);
        }

        [LocalDbFact]
        public async Task MarkUnavailableAsync_PersistsStatusAndStaleDelay()
        {
            var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
            var staleAfter = now.Add(Constants.ChannelUnavailableStaleDelay);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-unavailable', N'https://www.youtube.com/channel/channel-unavailable', N'Unavailable', N'unavailable.png', N'playlist-unavailable', @oldStaleAfter, @visibleAfter);
                ",
                new
                {
                    oldStaleAfter = now.AddMinutes(-5),
                    visibleAfter = now.AddMinutes(-1)
                });

            await _repository.MarkUnavailableAsync("channel-unavailable", ChannelStatusReason.NotFound, now, staleAfter);

            var persisted = await QuerySingleAsync<(ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt, DateTimeOffset StaleAfter, DateTimeOffset VisibleAfter)>(
                @"
                SELECT Status, StatusReason, StatusUpdatedAt, StaleAfter, VisibleAfter
                FROM Channel
                WHERE Id = N'channel-unavailable';
                ");

            Assert.Equal(ChannelStatus.Unavailable, persisted.Status);
            Assert.Equal(ChannelStatusReason.NotFound, persisted.StatusReason);
            Assert.Equal(now, persisted.StatusUpdatedAt);
            Assert.Equal(staleAfter, persisted.StaleAfter);
            Assert.Equal(now.AddMinutes(-1), persisted.VisibleAfter);
        }

        [LocalDbFact]
        public async Task ClaimNextStaleChannelAsync_ReturnsNullWhenNoEligibleChannelsExist()
        {
            var listId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'fresh', N'https://www.youtube.com/channel/fresh', N'Fresh', N'a.png', N'playlist-a', @futureStaleAfter, @visibleAfter),
                    (N'not-visible', N'https://www.youtube.com/channel/not-visible', N'Not Visible', N'b.png', N'playlist-b', @staleAfter, @futureVisibleAfter),
                    (N'orphan', N'https://www.youtube.com/channel/orphan', N'Orphan', N'c.png', N'playlist-c', @staleAfter, @visibleAfter),
                    (N'unavailable', N'https://www.youtube.com/channel/unavailable', N'Unavailable', N'd.png', N'playlist-d', @staleAfter, @visibleAfter);

                UPDATE Channel
                SET Status = @status,
                    StatusReason = @statusReason,
                    StatusUpdatedAt = @now
                WHERE Id = N'unavailable';

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'fresh'),
                    (@listId, N'not-visible'),
                    (@listId, N'unavailable');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)3, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-10),
                    futureStaleAfter = now.AddMinutes(10),
                    visibleAfter = now.AddMinutes(-1),
                    futureVisibleAfter = now.AddMinutes(10),
                    status = ChannelStatus.Unavailable,
                    statusReason = ChannelStatusReason.NotFound,
                    now
                });

            var claimed = await _repository.ClaimNextStaleChannelAsync(now, now.AddMinutes(5));

            Assert.Null(claimed);
        }

        [LocalDbFact]
        public async Task ClaimNextStaleChannelAsync_ConcurrentCallsReturnSingleWinner()
        {
            var listId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;

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
                    token = Enumerable.Repeat((byte)8, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-10),
                    visibleAfter = now.AddMinutes(-1)
                });

            var firstClaim = _repository.ClaimNextStaleChannelAsync(now, now.AddMinutes(5));
            var secondClaim = _repository.ClaimNextStaleChannelAsync(now, now.AddMinutes(5));

            var claims = await Task.WhenAll(firstClaim, secondClaim);

            var winner = Assert.Single(claims, claim => claim != null);
            Assert.Equal("eligible", winner.Id);
            Assert.Equal("https://www.youtube.com/channel/eligible", winner.Url);
            Assert.Equal("Eligible", winner.Title);
            Assert.Equal("a.png", winner.Thumbnail);
        }

        [LocalDbFact]
        public async Task ClaimNextStaleChannelAsync_DoesNotReissueLeaseUntilItExpires()
        {
            var listId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var leaseExpiresAt = now.AddMinutes(5);

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
                    token = Enumerable.Repeat((byte)9, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-10),
                    visibleAfter = now.AddMinutes(-1)
                });

            var firstClaim = await _repository.ClaimNextStaleChannelAsync(now, leaseExpiresAt);
            var beforeExpiryClaim = await _repository.ClaimNextStaleChannelAsync(leaseExpiresAt.AddSeconds(-1), leaseExpiresAt.AddMinutes(5));
            var afterExpiryClaim = await _repository.ClaimNextStaleChannelAsync(leaseExpiresAt.AddSeconds(1), leaseExpiresAt.AddMinutes(5));

            Assert.NotNull(firstClaim);
            Assert.Null(beforeExpiryClaim);
            Assert.NotNull(afterExpiryClaim);
            Assert.Equal("eligible", afterExpiryClaim.Id);
        }

        [LocalDbFact]
        public async Task ClaimStaleBatchAsync_LeasesOnlyEligibleSubscribedActiveChannels()
        {
            var listId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var leaseExpiresAt = now.AddMinutes(5);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'eligible-a', N'https://www.youtube.com/channel/eligible-a', N'A', N'a.png', N'playlist-a', @staleAfter1, @visibleAfter),
                    (N'eligible-b', N'https://www.youtube.com/channel/eligible-b', N'B', N'b.png', N'playlist-b', @staleAfter2, @visibleAfter),
                    (N'fresh', N'https://www.youtube.com/channel/fresh', N'Fresh', N'f.png', N'playlist-f', @futureStaleAfter, @visibleAfter),
                    (N'leased', N'https://www.youtube.com/channel/leased', N'Leased', N'l.png', N'playlist-l', @staleAfter1, @futureVisibleAfter),
                    (N'orphan', N'https://www.youtube.com/channel/orphan', N'Orphan', N'o.png', N'playlist-o', @staleAfter1, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'eligible-a'),
                    (@listId, N'eligible-b'),
                    (@listId, N'fresh'),
                    (@listId, N'leased');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)4, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter1 = now.AddMinutes(-10),
                    staleAfter2 = now.AddMinutes(-5),
                    futureStaleAfter = now.AddMinutes(10),
                    visibleAfter = now.AddMinutes(-1),
                    futureVisibleAfter = now.AddMinutes(10)
                });

            var claimed = await _repository.ClaimStaleBatchAsync(
                now,
                leaseExpiresAt,
                10,
                CancellationToken.None);
            var visibleAfter = await QueryAsync<(string Id, DateTimeOffset VisibleAfter)>(
                @"
                SELECT Id, VisibleAfter
                FROM Channel
                WHERE Id IN (N'eligible-a', N'eligible-b', N'fresh', N'leased', N'orphan');
                ");

            Assert.Equal(new[] { "eligible-a", "eligible-b" }, claimed.Select(channel => channel.Id).ToArray());
            Assert.Equal(
                new[] { "eligible-a", "eligible-b" },
                visibleAfter
                    .Where(channel => channel.VisibleAfter == leaseExpiresAt)
                    .Select(channel => channel.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray());
        }

        [LocalDbFact]
        public async Task GetNextActiveSubscribedRefreshAtAsync_UsesLaterOfStaleAndVisibleTimes()
        {
            var listId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var staleSoonVisibleLater = now.AddMinutes(10);
            var staleLaterVisibleSoon = now.AddMinutes(20);

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES
                    (N'leased-first', N'https://www.youtube.com/channel/leased-first', N'Leased', N'l.png', N'playlist-l', @now, @staleSoonVisibleLater),
                    (N'stale-later', N'https://www.youtube.com/channel/stale-later', N'Stale Later', N's.png', N'playlist-s', @staleLaterVisibleSoon, @now),
                    (N'orphan-now', N'https://www.youtube.com/channel/orphan-now', N'Orphan', N'o.png', N'playlist-o', @now, @now);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'leased-first'),
                    (@listId, N'stale-later');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)5, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    now,
                    staleSoonVisibleLater,
                    staleLaterVisibleSoon
                });

            var next = await _repository.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None);

            Assert.Equal(staleSoonVisibleLater, next);
        }
    }
}
