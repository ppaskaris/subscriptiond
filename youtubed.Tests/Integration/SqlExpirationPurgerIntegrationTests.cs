using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlExpirationPurgerIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeAppClock _clock;
        private readonly SqlExpirationPurger _purger;

        public SqlExpirationPurgerIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _clock = new FakeAppClock();
            _purger = new SqlExpirationPurger(
                new ListRepository(fixture.ConnectionFactory),
                new ShareLinkRepository(fixture.ConnectionFactory),
                new ChannelRepository(fixture.ConnectionFactory),
                _clock);
        }

        [LocalDbFact]
        public async Task PurgeExpiredListsAsync_DeletesOnlyExpiredLists()
        {
            var now = new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

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
                    expiredAfter = now.AddMinutes(-1),
                    activeAfter = now.AddMinutes(10)
                });

            var removed = await _purger.PurgeExpiredListsAsync(CancellationToken.None);
            var remainingTitles = await QueryAsync<string>("SELECT Title FROM List ORDER BY Title;");

            Assert.Equal(1, removed);
            Assert.Equal(new[] { "Active" }, remainingTitles);
        }

        [LocalDbFact]
        public async Task PurgeExpiredShareLinksAsync_DeletesOnlyLinksPastRetentionWindow()
        {
            var now = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)10, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES
                    (N'keep-link', @listId, @createdAt, @keepExpiresAfter, NULL),
                    (N'delete-link', @listId, @createdAt, @deleteExpiresAfter, NULL);
                ",
                new
                {
                    listId,
                    createdAt = now.AddDays(-3),
                    keepExpiresAfter = now.AddHours(-12),
                    deleteExpiresAfter = now.Subtract(Constants.ShareLinkRetentionAfterExpiration).AddMinutes(-1)
                });

            var removed = await _purger.PurgeExpiredShareLinksAsync(CancellationToken.None);
            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(1, removed);
            Assert.Equal(new[] { "keep-link" }, remaining);
        }

        [LocalDbFact]
        public async Task PurgeExpiredChannelsAsync_DeletesAllOrphanChannels()
        {
            var listId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
            _clock.UtcNow = now;

            await ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'List', @expiredAfter);

                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES
                    (N'orphan-expired', N'https://www.youtube.com/channel/orphan-expired', N'Orphan Expired', N'a.png', N'playlist-a', @staleAfter),
                    (N'orphan-active', N'https://www.youtube.com/channel/orphan-active', N'Orphan Active', N'b.png', N'playlist-b', @staleAfter),
                    (N'attached-expired', N'https://www.youtube.com/channel/attached-expired', N'Attached Expired', N'c.png', N'playlist-c', @staleAfter);

                INSERT INTO ListChannel (ListId, ChannelId)
                VALUES (@listId, N'attached-expired');
                ",
                new
                {
                    listId,
                    token = Enumerable.Repeat((byte)5, 40).ToArray(),
                    expiredAfter = now.AddDays(1),
                    staleAfter = now.AddMinutes(-1),
                });

            var removed = await _purger.PurgeExpiredChannelsAsync(CancellationToken.None);
            var remaining = await QueryAsync<string>("SELECT Id FROM Channel ORDER BY Id;");

            Assert.Equal(2, removed);
            Assert.Equal(new[] { "attached-expired" }, remaining);
        }

        private Task SeedListAsync(Guid listId, byte[] token)
        {
            return ExecuteAsync(
                @"
                INSERT INTO List (Id, Token, Title, ExpiredAfter)
                VALUES (@listId, @token, N'Share List', @expiredAfter);
                ",
                new
                {
                    listId,
                    token,
                    expiredAfter = DateTimeOffset.UtcNow.AddDays(1)
                });
        }
    }
}
