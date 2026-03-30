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
    }
}
