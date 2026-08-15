using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.DataTransfer;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlImportSourceIntegrationTests : LocalDbIntegrationTestBase
    {
        public SqlImportSourceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
        }

        [LocalDbFact]
        public async Task ReadAsync_UsesBoundedPagesAndSkipsExpiredAndUnreferencedData()
        {
            var importedAt = DateTimeOffset.UtcNow;
            var activeListId = Guid.NewGuid();
            var expiredListId = Guid.NewGuid();
            await ExecuteAsync(
                @"INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                  VALUES
                      ('referenced-channel', NULL, N'Referenced', N'referenced.jpg', 'playlist-1', @importedAt),
                      ('expired-channel', NULL, N'Expired only', N'expired.jpg', 'playlist-2', @importedAt),
                      ('unreferenced-channel', NULL, N'Unreferenced', N'unreferenced.jpg', 'playlist-3', @importedAt);

                  INSERT INTO [List] (Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn)
                  VALUES
                      (@activeListId, @token, N'Active', 1.25, @future, @renewedOn),
                      (@expiredListId, @expiredToken, N'Boundary expired', 1.00, @importedAt, NULL);

                  INSERT INTO ListChannel (ListId, ChannelId)
                  VALUES
                      (@activeListId, 'referenced-channel'),
                      (@expiredListId, 'expired-channel');

                  INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                  VALUES ('referenced-channel', 'video-1', N'Video', @duration, @publishedAt, N'video.jpg');

                  INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                  VALUES (N'ignored-password', @activeListId, @publishedAt, @future, NULL);",
                new
                {
                    activeListId,
                    expiredListId,
                    token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray(),
                    expiredToken = Enumerable.Repeat((byte)99, 40).ToArray(),
                    importedAt,
                    future = importedAt.AddHours(1),
                    renewedOn = DateOnly.FromDateTime(importedAt.UtcDateTime),
                    duration = TimeSpan.FromMinutes(2).Ticks,
                    publishedAt = importedAt.AddMinutes(-10)
                });

            var source = new SqlImportSource(Fixture.ConnectionString);
            var lists = await ReadAllAsync(source.ReadListsAsync(importedAt, 1, CancellationToken.None));
            var channels = await ReadAllAsync(source.ReadChannelsAsync(importedAt, 1, CancellationToken.None));

            var list = Assert.Single(lists);
            Assert.Equal(activeListId.ToString("D"), list.Id);
            Assert.Equal(new[] { "referenced-channel" }, list.ChannelIds);
            Assert.Equal(DateOnly.FromDateTime(importedAt.UtcDateTime), list.ExpirationRenewedOn);
            var channel = Assert.Single(channels);
            Assert.Equal("referenced-channel", channel.Id);
            var video = Assert.Single(channel.Videos);
            Assert.Equal("video-1", video.Id);
            Assert.Equal(TimeSpan.FromMinutes(2).Ticks, video.DurationTicks);
        }

        private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> values)
        {
            var result = new List<T>();
            await foreach (var value in values)
            {
                result.Add(value);
            }
            return result;
        }
    }
}
