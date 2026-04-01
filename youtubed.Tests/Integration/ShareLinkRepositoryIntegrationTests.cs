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
    public sealed class ShareLinkRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ShareLinkRepository _repository;

        public ShareLinkRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ShareLinkRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task GetByListAsync_ReturnsNewestFirstWithStatusesRepresented()
        {
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)4, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES
                    (N'older-link', @listId, @olderCreatedAt, @futureExpiry, NULL),
                    (N'used-link', @listId, @middleCreatedAt, @futureExpiry, @usedAt),
                    (N'newer-link', @listId, @newerCreatedAt, @pastExpiry, NULL);
                ",
                new
                {
                    listId,
                    olderCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
                    middleCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
                    newerCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    futureExpiry = DateTimeOffset.UtcNow.AddMinutes(30),
                    pastExpiry = DateTimeOffset.UtcNow.AddMinutes(-5),
                    usedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
                });

            var shareLinks = await _repository.GetByListAsync(listId);

            Assert.Equal(new[] { "newer-link", "used-link", "older-link" }, shareLinks.Select(link => link.Password).ToArray());
            Assert.Null(shareLinks.Single(link => link.Password == "older-link").UsedAt);
            Assert.NotNull(shareLinks.Single(link => link.Password == "used-link").UsedAt);
        }

        [LocalDbFact]
        public async Task ConsumeAsync_SucceedsOnceAndThenReturnsNull()
        {
            var listId = Guid.NewGuid();
            var token = Enumerable.Repeat((byte)5, 40).ToArray();

            await SeedListAsync(listId, token);
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES (N'single-use-link', @listId, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listId,
                    createdAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    expiresAfter = DateTimeOffset.UtcNow.AddMinutes(30)
                });

            var first = await _repository.ConsumeAsync("single-use-link", DateTimeOffset.UtcNow);
            var second = await _repository.ConsumeAsync("single-use-link", DateTimeOffset.UtcNow.AddSeconds(1));
            var usedAt = await QuerySingleOrDefaultAsync<DateTimeOffset?>(
                "SELECT UsedAt FROM ShareLink WHERE Password = N'single-use-link';");

            Assert.NotNull(first);
            Assert.Equal(listId, first.ListId);
            Assert.Equal(token, first.Token);
            Assert.Null(second);
            Assert.NotNull(usedAt);
        }

        [LocalDbFact]
        public async Task ConsumeAsync_ReturnsNullForExpiredLink()
        {
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)6, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES (N'expired-link', @listId, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listId,
                    createdAt = DateTimeOffset.UtcNow.AddMinutes(-90),
                    expiresAfter = DateTimeOffset.UtcNow.AddMinutes(-1)
                });

            var consumed = await _repository.ConsumeAsync("expired-link", DateTimeOffset.UtcNow);

            Assert.Null(consumed);
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
