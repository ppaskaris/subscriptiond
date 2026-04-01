using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ShareLinkServiceIntegrationTests : LocalDbIntegrationTestBase
    {
        private static readonly Regex PasswordPattern = new Regex("^[a-z]+(-[a-z]+){3}$");

        private readonly ShareLinkService _service;

        public ShareLinkServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _service = new ShareLinkService(new ShareLinkRepository(fixture.ConnectionFactory));
        }

        [LocalDbFact]
        public async Task CreateShareLinkAsync_PersistsUniquePasswordWithExpectedExpiryWindow()
        {
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)7, 40).ToArray());

            var beforeCreate = DateTimeOffset.Now;
            var shareLink = await _service.CreateShareLinkAsync(listId);
            var persisted = await QuerySingleAsync<ShareLinkModel>(
                @"
                SELECT Password, ListId, CreatedAt, ExpiresAfter, UsedAt
                FROM ShareLink
                WHERE Password = @password;
                ",
                new { password = shareLink.Password });

            Assert.Equal(listId, shareLink.ListId);
            Assert.Matches(PasswordPattern, shareLink.Password);
            Assert.Equal(listId, persisted.ListId);
            Assert.Equal(shareLink.Password, persisted.Password);
            Assert.InRange(
                persisted.ExpiresAfter - persisted.CreatedAt,
                Constants.ShareLinkMaxAgeMin,
                Constants.ShareLinkMaxAgeMax);
            Assert.True(persisted.CreatedAt >= beforeCreate.AddSeconds(-1));
        }

        [LocalDbFact]
        public async Task DeleteShareLinksAsync_RemovesOnlyTargetListRows()
        {
            var listA = Guid.NewGuid();
            var listB = Guid.NewGuid();

            await SeedListAsync(listA, Enumerable.Repeat((byte)8, 40).ToArray());
            await SeedListAsync(listB, Enumerable.Repeat((byte)9, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES
                    (N'list-a-link', @listA, @createdAt, @expiresAfter, NULL),
                    (N'list-b-link', @listB, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listA,
                    listB,
                    createdAt = DateTimeOffset.UtcNow,
                    expiresAfter = DateTimeOffset.UtcNow.AddMinutes(30)
                });

            await _service.DeleteShareLinksAsync(listA);

            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(new[] { "list-b-link" }, remaining);
        }

        [LocalDbFact]
        public async Task DeleteShareLinkAsync_RemovesOnlyTargetPassword()
        {
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)8, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES
                    (N'keep-link', @listId, @createdAt, @expiresAfter, NULL),
                    (N'delete-link', @listId, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listId,
                    createdAt = DateTimeOffset.UtcNow,
                    expiresAfter = DateTimeOffset.UtcNow.AddMinutes(30)
                });

            await _service.DeleteShareLinkAsync("delete-link");

            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(new[] { "keep-link" }, remaining);
        }

        [LocalDbFact]
        public async Task DeleteShareLinkAsync_DoesNotAffectOtherLists()
        {
            var listA = Guid.NewGuid();
            var listB = Guid.NewGuid();

            await SeedListAsync(listA, Enumerable.Repeat((byte)8, 40).ToArray());
            await SeedListAsync(listB, Enumerable.Repeat((byte)9, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES
                    (N'list-a-link', @listA, @createdAt, @expiresAfter, NULL),
                    (N'list-b-link', @listB, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listA,
                    listB,
                    createdAt = DateTimeOffset.UtcNow,
                    expiresAfter = DateTimeOffset.UtcNow.AddMinutes(30)
                });

            await _service.DeleteShareLinkAsync("list-a-link");

            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(new[] { "list-b-link" }, remaining);
        }

        [LocalDbFact]
        public async Task RemoveExpiredShareLinksAsync_DeletesOnlyRowsPastRetentionWindow()
        {
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
                    createdAt = DateTimeOffset.UtcNow.AddDays(-3),
                    keepExpiresAfter = DateTimeOffset.UtcNow.AddHours(-12),
                    deleteExpiresAfter = DateTimeOffset.UtcNow.Subtract(Constants.ShareLinkRetentionAfterExpiration).AddMinutes(-1)
                });

            var removed = await _service.RemoveExpiredShareLinksAsync();
            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(1, removed);
            Assert.Equal(new[] { "keep-link" }, remaining);
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
