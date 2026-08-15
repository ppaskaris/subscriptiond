using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
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
    [Trait("Category", "Cosmos")]
    public sealed class SqlToCosmosImportIntegrationTests
    {
        [CosmosFact]
        public async Task Import_RecoversAfterDurableInterruptionAndMatchesProviderBehavior()
        {
            var source = new LocalDbTestFixture();
            var target = new CosmosTestFixture();
            await source.InitializeAsync();
            await target.InitializeAsync();

            try
            {
                var importedAt = DateTimeOffset.UtcNow;
                var migrationClock = new FakeAppClock { UtcNow = importedAt };
                var listId = Guid.NewGuid();
                var expiredListId = Guid.NewGuid();
                var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
                await SeedSourceAsync(source, importedAt, listId, expiredListId, token);

                var importTarget = new CosmosImportTarget(target.Context);
                using var output = new StringWriter();
                for (var interruptionPoint = 1; interruptionPoint <= 3; interruptionPoint++)
                {
                    if (interruptionPoint > 1)
                    {
                        await ClearTargetAsync(target, listId);
                    }

                    var interrupted = new SqlToCosmosImportService(
                        new SqlImportSource(source.ConnectionString),
                        new InterruptAfterDurableWriteTarget(importTarget, interruptionPoint),
                        output,
                        migrationClock);
                    await Assert.ThrowsAsync<SimulatedInterruptionException>(() => interrupted.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Import,
                            2,
                            ConfirmEmptyTarget: true,
                            ConfirmPreCutoverRerun: false),
                        importedAt,
                        CancellationToken.None));

                    var recovery = new SqlToCosmosImportService(
                        new SqlImportSource(source.ConnectionString),
                        importTarget,
                        output,
                        migrationClock);
                    await recovery.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Import,
                            2,
                            ConfirmEmptyTarget: false,
                            ConfirmPreCutoverRerun: true),
                        importedAt,
                        CancellationToken.None);
                    await recovery.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Reconcile,
                            2,
                            ConfirmEmptyTarget: false,
                            ConfirmPreCutoverRerun: false),
                        importedAt,
                        CancellationToken.None);
                }

                var expectedDocument = Assert.Single(await ReadAllAsync(
                    new SqlImportSource(source.ConnectionString).ReadListsAsync(
                        importedAt,
                        2,
                        CancellationToken.None)));
                var actualDocument = Assert.Single(await ReadAllAsync(
                    importTarget.ReadListsAsync(2, CancellationToken.None)));
                Assert.Equal(expectedDocument.Id, actualDocument.Id);
                Assert.Equal(expectedDocument.Token, actualDocument.Token);
                Assert.Equal(expectedDocument.Title, actualDocument.Title);
                Assert.Equal(expectedDocument.PlaybackRate, actualDocument.PlaybackRate);
                Assert.Equal(expectedDocument.ExpiredAfter, actualDocument.ExpiredAfter);
                Assert.Equal(expectedDocument.ExpirationRenewedOn, actualDocument.ExpirationRenewedOn);
                Assert.Equal(expectedDocument.ChannelIds, actualDocument.ChannelIds);
                Assert.Equal(expectedDocument.Ttl, actualDocument.Ttl);
                Assert.NotNull(actualDocument.ETag);
                var lists = new CosmosListRepository(
                    target.Context,
                    migrationClock,
                    NullLogger<CosmosListRepository>.Instance);
                var channels = new CosmosChannelRepository(
                    target.Context,
                    NullLogger<CosmosChannelRepository>.Instance);
                var importedList = await lists.GetAsync(listId);
                Assert.NotNull(importedList);
                Assert.Equal(token, importedList.Token);
                Assert.Equal("Representative list", importedList.Title);
                Assert.Equal(1.50m, importedList.PlaybackRate);
                Assert.Equal(importedAt.AddDays(2), importedList.ExpiredAfter);
                Assert.Equal(DateOnly.FromDateTime(importedAt.UtcDateTime).AddDays(-1), importedList.ExpirationRenewedOn);

                var projection = await lists.GetChannelProjectionAsync(importedList);
                Assert.Equal(new[] { "channel-active", "channel-unavailable" }, projection.ChannelIds);
                Assert.All(projection.Channels, channel => Assert.False(channel.IsMissing));

                var unavailable = await channels.GetByIdAsync("channel-unavailable");
                Assert.Equal(ChannelStatus.Unavailable, unavailable.Status);
                Assert.Equal(ChannelStatusReason.NotFound, unavailable.StatusReason);
                Assert.Equal(importedAt.AddHours(-3), unavailable.StatusUpdatedAt);
                Assert.Equal(importedAt.AddHours(-2), unavailable.StaleAfter);

                var active = await channels.GetByIdAsync("channel-active");
                Assert.Equal(100, active.Videos.Count);
                Assert.Equal("video-100", active.Videos[0].VideoId);
                Assert.Equal("video-001", active.Videos[^1].VideoId);

                Assert.Null(await lists.GetAsync(expiredListId));
                Assert.Null(await channels.GetByIdAsync("expired-only-channel"));
                Assert.Null(await channels.GetByIdAsync("unreferenced-channel"));
                Assert.Equal(0, await importTarget.CountShareLinksAsync(CancellationToken.None));

                var listDocument = await target.Context.Lists.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
                Assert.InRange(listDocument.Resource.Ttl, 1, checked((int)TimeSpan.FromDays(2).TotalSeconds));
                Assert.True(
                    CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(listDocument.Resource) < 512 * 1024);
                var channelDocument = await target.Context.Channels.ReadItemAsync<CosmosChannelDocument>(
                    "channel-active",
                    new Microsoft.Azure.Cosmos.PartitionKey("channel-active"));
                Assert.True(
                    CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(channelDocument.Resource) < 512 * 1024);

                var safeOutput = output.ToString();
                Assert.DoesNotContain(Convert.ToBase64String(token), safeOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("Representative list", safeOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("share-password", safeOutput, StringComparison.Ordinal);
                Assert.Contains("Lists=1 Channels=2", safeOutput, StringComparison.Ordinal);
            }
            finally
            {
                await target.DisposeAsync();
                await source.DisposeAsync();
            }
        }

        private static async Task SeedSourceAsync(
            LocalDbTestFixture source,
            DateTimeOffset importedAt,
            Guid listId,
            Guid expiredListId,
            byte[] token)
        {
            await using var connection = source.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                @"INSERT INTO Channel
                      (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                  VALUES
                      ('channel-active', N'https://example.test/active', N'Active channel', N'https://example.test/active.jpg', 'playlist-active', @activeStale, 0, 0, NULL),
                      ('channel-unavailable', N'https://example.test/unavailable', N'Unavailable channel', N'https://example.test/unavailable.jpg', 'playlist-unavailable', @unavailableStale, 1, 1, @statusUpdatedAt),
                      ('expired-only-channel', N'https://example.test/expired', N'Expired channel', N'https://example.test/expired.jpg', 'playlist-expired', @activeStale, 0, 0, NULL),
                      ('unreferenced-channel', N'https://example.test/unreferenced', N'Unreferenced channel', N'https://example.test/unreferenced.jpg', 'playlist-unreferenced', @activeStale, 0, 0, NULL);

                  INSERT INTO [List]
                      (Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn)
                  VALUES
                      (@listId, @token, N'Representative list', 1.50, @expiresAfter, @renewedOn),
                      (@expiredListId, @expiredToken, N'Expired list', 1.00, @importedAt, NULL);

                  INSERT INTO ListChannel (ListId, ChannelId)
                  VALUES
                      (@listId, 'channel-unavailable'),
                      (@listId, 'channel-active'),
                      (@expiredListId, 'expired-only-channel');

                  INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                  VALUES (N'share-password', @listId, @createdAt, @expiresAfter, NULL);

                  CREATE TABLE WorkerState (Id INT NOT NULL PRIMARY KEY, NextRunAt DATETIMEOFFSET NULL);
                  INSERT INTO WorkerState (Id, NextRunAt) VALUES (1, @createdAt);",
                new
                {
                    listId,
                    expiredListId,
                    token,
                    expiredToken = Enumerable.Repeat((byte)99, 40).ToArray(),
                    importedAt,
                    expiresAfter = importedAt.AddDays(2),
                    renewedOn = DateOnly.FromDateTime(importedAt.UtcDateTime).AddDays(-1),
                    createdAt = importedAt.AddHours(-4),
                    activeStale = importedAt.AddHours(1),
                    unavailableStale = importedAt.AddHours(-2),
                    statusUpdatedAt = importedAt.AddHours(-3)
                });

            var videos = Enumerable.Range(0, 101).Select(value => new
            {
                ChannelId = "channel-active",
                Id = $"video-{value:D3}",
                Title = $"Video {value}",
                Duration = TimeSpan.FromMinutes(value + 1).Ticks,
                PublishedAt = importedAt.AddMinutes(value),
                Thumbnail = $"https://example.test/video-{value:D3}.jpg"
            });
            await connection.ExecuteAsync(
                @"INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                  VALUES (@ChannelId, @Id, @Title, @Duration, @PublishedAt, @Thumbnail);",
                videos);
        }

        private static async Task ClearTargetAsync(CosmosTestFixture target, Guid listId)
        {
            await target.Context.Lists.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
            foreach (var channelId in new[] { "channel-active", "channel-unavailable" })
            {
                await target.Context.Channels.DeleteItemAsync<CosmosChannelDocument>(
                    channelId,
                    new Microsoft.Azure.Cosmos.PartitionKey(channelId));
            }
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

        private sealed class InterruptAfterDurableWriteTarget : ISqlToCosmosImportTarget
        {
            private readonly ISqlToCosmosImportTarget _inner;
            private readonly int _interruptionPoint;
            private int _writes;
            private bool _hasInterrupted;

            public InterruptAfterDurableWriteTarget(
                ISqlToCosmosImportTarget inner,
                int interruptionPoint)
            {
                _inner = inner;
                _interruptionPoint = interruptionPoint;
            }

            public IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
                int batchSize,
                CancellationToken cancellationToken) => _inner.ReadListsAsync(batchSize, cancellationToken);

            public IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
                int batchSize,
                CancellationToken cancellationToken) => _inner.ReadChannelsAsync(batchSize, cancellationToken);

            public Task<int> CountShareLinksAsync(CancellationToken cancellationToken)
                => _inner.CountShareLinksAsync(cancellationToken);

            public Task UpsertListAsync(CosmosListDocument document, CancellationToken cancellationToken)
                => UpsertAndInterruptAsync(
                    () => _inner.UpsertListAsync(document, cancellationToken));

            public async Task UpsertChannelAsync(
                CosmosChannelDocument document,
                CancellationToken cancellationToken)
            {
                await UpsertAndInterruptAsync(
                    () => _inner.UpsertChannelAsync(document, cancellationToken));
            }

            private async Task UpsertAndInterruptAsync(Func<Task> upsert)
            {
                await upsert();
                _writes++;
                if (!_hasInterrupted && _writes == _interruptionPoint)
                {
                    _hasInterrupted = true;
                    throw new SimulatedInterruptionException();
                }
            }
        }

        private sealed class SimulatedInterruptionException : Exception
        {
        }
    }
}
