using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
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
        public async Task SaveDiscoveredChannelAsync_DoesNotOverwriteExistingMetadata()
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

            var persisted = await QuerySingleAsync<(string Id, string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter)>(
                @"
                SELECT Id, Title, Thumbnail, PlaylistId, StaleAfter
                FROM Channel
                WHERE Url = N'https://www.youtube.com/channel/channel-1';
                ");

            Assert.Equal("channel-1", persisted.Id);
            Assert.Equal("Original", persisted.Title);
            Assert.Equal("old.png", persisted.Thumbnail);
            Assert.Equal("playlist-old", persisted.PlaylistId);
            Assert.True(persisted.StaleAfter <= DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        [LocalDbFact]
        public async Task SaveDiscoveredChannelAsync_ConcurrentCallsLeaveSingleRow()
        {
            const string url = "https://www.youtube.com/channel/shared";
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
                    Id = "channel-2",
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
            Assert.Contains(persisted.Id, new[] { "channel-1", "channel-2" });
            Assert.True(persisted.StaleAfter >= staleAfter);

            if (persisted.Id == "channel-1")
            {
                Assert.Equal("Original", persisted.Title);
                Assert.Equal("original.png", persisted.Thumbnail);
                Assert.Equal("playlist-original", persisted.PlaylistId);
            }
            else
            {
                Assert.Equal("Updated", persisted.Title);
                Assert.Equal("updated.png", persisted.Thumbnail);
                Assert.Equal("playlist-updated", persisted.PlaylistId);
            }
        }

        [LocalDbFact]
        public async Task UpdateMetadataAsync_UpdatesOnlyTitleAndThumbnail()
        {
            var staleAfter = DateTimeOffset.UtcNow.AddHours(1);
            var visibleAfter = DateTimeOffset.UtcNow.AddMinutes(30);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Original', N'old.png', N'playlist-1', @staleAfter, @visibleAfter);
                ",
                new { staleAfter, visibleAfter });

            await _repository.UpdateMetadataAsync("channel-1", "Updated", "new.png");

            var persisted = await QuerySingleAsync<(string Url, string Title, string Thumbnail, string PlaylistId, DateTimeOffset StaleAfter, DateTimeOffset VisibleAfter)>(
                @"
                SELECT Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter
                FROM Channel
                WHERE Id = N'channel-1';
                ");

            Assert.Equal("https://www.youtube.com/channel/channel-1", persisted.Url);
            Assert.Equal("Updated", persisted.Title);
            Assert.Equal("new.png", persisted.Thumbnail);
            Assert.Equal("playlist-1", persisted.PlaylistId);
            Assert.Equal(staleAfter, persisted.StaleAfter);
            Assert.Equal(visibleAfter, persisted.VisibleAfter);
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
                    (N'orphan', N'https://www.youtube.com/channel/orphan', N'Orphan', N'c.png', N'playlist-c', @staleAfter, @visibleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES
                    (@listId, N'fresh'),
                    (@listId, N'not-visible');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)3, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-10),
                    futureStaleAfter = now.AddMinutes(10),
                    visibleAfter = now.AddMinutes(-1),
                    futureVisibleAfter = now.AddMinutes(10)
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
    }
}
