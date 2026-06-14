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

        private readonly FakeAppClock _clock;
        private readonly ShareLinkService _service;

        public ShareLinkServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _clock = new FakeAppClock();
            _service = new ShareLinkService(new ShareLinkRepository(fixture.ConnectionFactory), _clock);
        }

        [LocalDbFact]
        public async Task CreateShareLinkAsync_PersistsUniquePasswordWithExpectedExpiryWindow()
        {
            _clock.UtcNow = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)7, 40).ToArray());

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
            Assert.Equal(_clock.UtcNow, persisted.CreatedAt);
            Assert.Equal(_clock.UtcNow.Add(Constants.ShareLinkMaxAgeMin), persisted.ExpiresAfter);
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
        public async Task DeleteShareLinkInListAsync_RemovesOnlyTargetPassword()
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

            await _service.DeleteShareLinkInListAsync(listId, "delete-link");

            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(new[] { "keep-link" }, remaining);
        }

        [LocalDbFact]
        public async Task DeleteShareLinkInListAsync_DoesNotAffectOtherLists()
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

            await _service.DeleteShareLinkInListAsync(listA, "list-a-link");

            var remaining = await QueryAsync<string>("SELECT Password FROM ShareLink ORDER BY Password;");

            Assert.Equal(new[] { "list-b-link" }, remaining);
        }

        [LocalDbFact]
        public async Task ConsumeShareLinkAsync_UsesClockTime()
        {
            _clock.UtcNow = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();

            await SeedListAsync(listId, Enumerable.Repeat((byte)10, 40).ToArray());
            await ExecuteAsync(
                @"
                INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                VALUES (N'consume-link', @listId, @createdAt, @expiresAfter, NULL);
                ",
                new
                {
                    listId,
                    createdAt = _clock.UtcNow.AddMinutes(-10),
                    expiresAfter = _clock.UtcNow.AddMinutes(30)
                });

            var consumed = await _service.ConsumeShareLinkAsync("consume-link");
            var usedAt = await ScalarAsync<DateTimeOffset>(
                "SELECT UsedAt FROM ShareLink WHERE Password = N'consume-link';");

            Assert.NotNull(consumed);
            Assert.Equal(listId, consumed.ListId);
            Assert.Equal(_clock.UtcNow, usedAt);
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
