using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using youtubed.DataTransfer;
using youtubed.Domain;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class DataTransferCliIntegrationTests : LocalDbIntegrationTestBase
    {
        public DataTransferCliIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
        }

        [LocalDbFact]
        public async Task RunAsync_CopiesAllAppDataToTargetDatabase()
        {
            var targetFixture = new LocalDbTestFixture();
            await targetFixture.InitializeAsync();

            try
            {
                var listId = Guid.NewGuid();
                var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
                var createdAt = DateTimeOffset.UtcNow.AddHours(-2);
                var expiresAfter = DateTimeOffset.UtcNow.AddDays(1);

                await ExecuteAsync(
                    @"
                    INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter, Status, StatusReason, StatusUpdatedAt)
                    VALUES ('channel-1', N'https://example.test/channel', N'Example Channel', N'https://example.test/thumb.jpg', 'playlist-1', @expiresAfter, @createdAt, @status, @statusReason, @statusUpdatedAt);

                    INSERT INTO [List] (Id, Token, Title, PlaybackRate, ExpiredAfter)
                    VALUES (@listId, @token, N'Example List', 1.50, @expiresAfter);

                    INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                    VALUES (N'one-time-password', @listId, @createdAt, @expiresAfter, NULL);

                    INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                    VALUES ('channel-1', 'video-1', N'Example Video', 12345, @createdAt, N'https://example.test/video.jpg');

                    INSERT INTO ListChannel (ListId, ChannelId)
                    VALUES (@listId, 'channel-1');
                    ",
                    new
                    {
                        listId,
                        token,
                        createdAt,
                        expiresAfter,
                        status = ChannelStatus.Unavailable,
                        statusReason = ChannelStatusReason.NotFound,
                        statusUpdatedAt = createdAt.AddMinutes(30)
                    });

                await using (var targetConnection = targetFixture.CreateConnection())
                {
                    await targetConnection.OpenAsync();
                    await Dapper.SqlMapper.ExecuteAsync(
                        targetConnection,
                        @"
                        INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                        VALUES ('old-channel', NULL, N'Old Channel', N'old.jpg', 'old-playlist', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
                        ");
                }

                var exitCode = await DataTransferCli.RunAsync(new[]
                {
                    "transfer-data",
                    "--SourceConnectionString",
                    Fixture.ConnectionString,
                    "--TargetConnectionString",
                    targetFixture.ConnectionString
                });

                Assert.Equal(0, exitCode);
                Assert.Equal(1, await CountTargetRowsAsync(targetFixture, "Channel"));
                Assert.Equal(1, await CountTargetRowsAsync(targetFixture, "List"));
                Assert.Equal(1, await CountTargetRowsAsync(targetFixture, "ShareLink"));
                Assert.Equal(1, await CountTargetRowsAsync(targetFixture, "ChannelVideo"));
                Assert.Equal(1, await CountTargetRowsAsync(targetFixture, "ListChannel"));

                await using var connection = targetFixture.CreateConnection();
                await connection.OpenAsync();
                var title = await Dapper.SqlMapper.QuerySingleAsync<string>(
                    connection,
                    "SELECT Title FROM [List] WHERE Id = @listId;",
                    new { listId });
                var playbackRate = await Dapper.SqlMapper.QuerySingleAsync<decimal>(
                    connection,
                    "SELECT PlaybackRate FROM [List] WHERE Id = @listId;",
                    new { listId });
                var staleAfter = await Dapper.SqlMapper.QuerySingleAsync<DateTimeOffset>(
                    connection,
                    "SELECT StaleAfter FROM Channel WHERE Id = 'channel-1';");
                var status = await Dapper.SqlMapper.QuerySingleAsync<(ChannelStatus Status, ChannelStatusReason StatusReason, DateTimeOffset? StatusUpdatedAt)>(
                    connection,
                    "SELECT Status, StatusReason, StatusUpdatedAt FROM Channel WHERE Id = 'channel-1';");

                Assert.Equal("Example List", title);
                Assert.Equal(1.50m, playbackRate);
                Assert.Equal(expiresAfter, staleAfter);
                Assert.Equal(ChannelStatus.Unavailable, status.Status);
                Assert.Equal(ChannelStatusReason.NotFound, status.StatusReason);
                Assert.Equal(createdAt.AddMinutes(30), status.StatusUpdatedAt);
            }
            finally
            {
                await targetFixture.DisposeAsync();
            }
        }

        private static async Task<int> CountTargetRowsAsync(LocalDbTestFixture fixture, string tableName)
        {
            await using var connection = fixture.CreateConnection();
            await connection.OpenAsync();
            return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
                connection,
                $"SELECT COUNT(*) FROM [{tableName}];");
        }
    }
}
