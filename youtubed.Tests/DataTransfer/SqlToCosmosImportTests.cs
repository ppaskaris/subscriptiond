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

namespace youtubed.Tests.DataTransfer
{
    public sealed class SqlToCosmosImportTests
    {
        [Fact]
        public void ListMapping_PreservesEveryFieldAndOrdersDistinctMembership()
        {
            var importedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var row = new SqlImportList(
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                token,
                "Mapped title",
                1.75m,
                importedAt.AddSeconds(61),
                new DateOnly(2026, 8, 13));

            var document = SqlToCosmosImportMapper.ToDocument(
                row,
                new[] { "channel-b", "channel-a", "channel-b" },
                importedAt);

            Assert.Equal(row.Id.ToString("D"), document.Id);
            Assert.Equal(token, document.Token);
            Assert.NotSame(token, document.Token);
            Assert.Equal(row.Title, document.Title);
            Assert.Equal(row.PlaybackRate, document.PlaybackRate);
            Assert.Equal(row.ExpiredAfter, document.ExpiredAfter);
            Assert.Equal(row.ExpirationRenewedOn, document.ExpirationRenewedOn);
            Assert.Equal(new[] { "channel-a", "channel-b" }, document.ChannelIds);
            Assert.Equal(61, document.Ttl);
        }

        [Fact]
        public void ListMapping_AcceptsMaximumMembershipAndRejectsOneMore()
        {
            var now = DateTimeOffset.UtcNow;
            var row = CreateList(now);
            var maximum = Enumerable.Range(0, 100).Select(value => $"channel-{value:D3}").ToArray();

            var document = SqlToCosmosImportMapper.ToDocument(row, maximum, now);

            Assert.Equal(100, document.ChannelIds.Count);
            Assert.Throws<ArgumentException>(() => SqlToCosmosImportMapper.ToDocument(
                row,
                maximum.Append("channel-100"),
                now));
        }

        [Fact]
        public void ChannelMapping_PreservesEveryFieldAndKeepsNewestDistinctHundredVideos()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var row = new SqlImportChannel(
                "channel-id",
                "https://example.test/channel",
                "Channel title",
                "https://example.test/channel.jpg",
                "playlist-id",
                now.AddMinutes(-5),
                ChannelStatus.Unavailable,
                ChannelStatusReason.Private,
                now.AddMinutes(-10));
            var videos = Enumerable.Range(0, 101)
                .Select(value => new ChannelVideo
                {
                    ChannelId = row.Id,
                    VideoId = $"video-{value:D3}",
                    Title = $"Video {value}",
                    Duration = TimeSpan.FromTicks(value + 1),
                    PublishedAt = now.AddMinutes(value),
                    ThumbnailUrl = $"https://example.test/{value}.jpg"
                })
                .Append(new ChannelVideo
                {
                    ChannelId = row.Id,
                    VideoId = "video-100",
                    Title = "Older duplicate",
                    Duration = TimeSpan.Zero,
                    PublishedAt = now.AddYears(-1),
                    ThumbnailUrl = null
                });

            var document = SqlToCosmosImportMapper.ToDocument(row, videos);

            Assert.Equal(row.Id, document.Id);
            Assert.Equal(row.Url, document.Url);
            Assert.Equal(row.Title, document.Title);
            Assert.Equal(row.Thumbnail, document.Thumbnail);
            Assert.Equal(row.PlaylistId, document.PlaylistId);
            Assert.Equal(row.StaleAfter, document.StaleAfter);
            Assert.Equal("Unavailable", document.Status);
            Assert.Equal("Private", document.StatusReason);
            Assert.Equal(row.StatusUpdatedAt, document.StatusUpdatedAt);
            Assert.Equal(100, document.Videos.Count);
            Assert.Equal("video-100", document.Videos[0].Id);
            Assert.Equal("video-001", document.Videos[^1].Id);
            Assert.Equal(101, document.Videos[0].DurationTicks);
        }

        [Theory]
        [InlineData("Validate")]
        [InlineData("Reconcile")]
        public async Task ReadOnlyModes_DoNotMutateTargetOrExposeSecrets(string modeName)
        {
            var mode = Enum.Parse<SqlToCosmosImportMode>(modeName);
            var now = DateTimeOffset.UtcNow;
            var list = CreateListDocument(now);
            var channel = CreateChannelDocument();
            var source = new FakeSource(new[] { list }, new[] { channel });
            var target = mode == SqlToCosmosImportMode.Reconcile
                ? new FakeTarget(new[] { list }, new[] { channel })
                : new FakeTarget();
            using var output = new StringWriter();
            var clock = new FakeAppClock { UtcNow = now };
            var service = new SqlToCosmosImportService(source, target, output, clock);

            var result = await service.RunAsync(
                new SqlToCosmosImportOptions(mode, 10, false, false),
                now,
                CancellationToken.None);

            Assert.Equal(1, result.ListCount);
            Assert.Equal(1, result.ChannelCount);
            Assert.Equal(0, target.MutationCount);
            Assert.DoesNotContain(Convert.ToBase64String(list.Token), output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(list.Title, output.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Import_RequiresEmptyConfirmationThenCanRerunAnInterruptedSubset()
        {
            var now = DateTimeOffset.UtcNow;
            var lists = new[] { CreateListDocument(now), CreateListDocument(now, Guid.NewGuid()) };
            var channels = new[] { CreateChannelDocument(), CreateChannelDocument("channel-2") };
            var target = new FakeTarget();
            var service = new SqlToCosmosImportService(
                new FakeSource(lists, channels),
                target,
                TextWriter.Null,
                new FakeAppClock { UtcNow = now });

            var confirmationError = await Assert.ThrowsAsync<SqlToCosmosImportOperationException>(() => service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, false, false),
                now,
                CancellationToken.None));
            Assert.Equal(
                SqlToCosmosImportError.FirstImportConfirmationRequired,
                confirmationError.Error);

            target.ThrowAfterMutation = 1;
            await Assert.ThrowsAsync<SimulatedInterruptionException>(() => service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, true, false),
                now,
                CancellationToken.None));
            target.ThrowAfterMutation = null;

            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, false, true),
                now,
                CancellationToken.None);
            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Reconcile, 10, false, false),
                now,
                CancellationToken.None);

            Assert.Equal(2, target.Lists.Count);
            Assert.Equal(2, target.Channels.Count);
        }

        [Fact]
        public async Task Import_RejectsTargetMutationAndShareLinksBeforeWriting()
        {
            var now = DateTimeOffset.UtcNow;
            var expected = CreateListDocument(now);
            var mutated = CreateListDocument(now);
            mutated.Title = "Post-cutover title";
            var target = new FakeTarget(new[] { mutated }) { ShareLinkCount = 1 };
            var service = new SqlToCosmosImportService(
                new FakeSource(new[] { expected }, Array.Empty<CosmosChannelDocument>()),
                target,
                TextWriter.Null,
                new FakeAppClock { UtcNow = now });

            var targetError = await Assert.ThrowsAsync<SqlToCosmosImportOperationException>(() => service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, false, true),
                now,
                CancellationToken.None));

            Assert.Equal(SqlToCosmosImportError.TargetContainsShareLinks, targetError.Error);
            Assert.Equal(0, target.MutationCount);
        }

        [Fact]
        public async Task Import_ObservesCancellationBeforeTargetMutation()
        {
            var now = DateTimeOffset.UtcNow;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var target = new FakeTarget();
            var service = new SqlToCosmosImportService(
                new FakeSource(new[] { CreateListDocument(now) }, Array.Empty<CosmosChannelDocument>()),
                target,
                TextWriter.Null,
                new FakeAppClock { UtcNow = now });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, true, false),
                now,
                cancellation.Token));
            Assert.Equal(0, target.MutationCount);
        }

        [Fact]
        public async Task Import_RecomputesListTtlAtEachDelayedWriteAndRerun()
        {
            var importedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var firstList = CreateListDocument(
                importedAt,
                Guid.Parse("00000000-0000-0000-0000-000000000001"));
            var secondList = CreateListDocument(
                importedAt,
                Guid.Parse("00000000-0000-0000-0000-000000000002"));
            var clock = new FakeAppClock { UtcNow = importedAt };
            var target = new FakeTarget
            {
                AfterListUpsert = _ => clock.UtcNow = clock.UtcNow.AddMinutes(10)
            };
            var service = new SqlToCosmosImportService(
                new FakeSource(new[] { firstList, secondList }, Array.Empty<CosmosChannelDocument>()),
                target,
                TextWriter.Null,
                clock);

            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, true, false),
                importedAt,
                CancellationToken.None);
            Assert.Equal(checked((int)TimeSpan.FromDays(1).TotalSeconds), firstList.Ttl);
            Assert.Equal(
                checked((int)TimeSpan.FromMinutes(23 * 60 + 50).TotalSeconds),
                secondList.Ttl);

            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, false, true),
                importedAt,
                CancellationToken.None);
            Assert.Equal(
                checked((int)TimeSpan.FromMinutes(23 * 60 + 40).TotalSeconds),
                firstList.Ttl);
            Assert.Equal(
                checked((int)TimeSpan.FromMinutes(23 * 60 + 30).TotalSeconds),
                secondList.Ttl);
        }

        [Fact]
        public async Task Import_CancellationAfterDurableWriteCanRestartSafely()
        {
            var importedAt = DateTimeOffset.UtcNow;
            var list = CreateListDocument(importedAt);
            var channel = CreateChannelDocument();
            using var cancellation = new CancellationTokenSource();
            var target = new FakeTarget
            {
                CancellationSource = cancellation,
                CancelAfterMutation = 1
            };
            var service = new SqlToCosmosImportService(
                new FakeSource(new[] { list }, new[] { channel }),
                target,
                TextWriter.Null,
                new FakeAppClock { UtcNow = importedAt });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, true, false),
                importedAt,
                cancellation.Token));
            Assert.Single(target.Channels);
            Assert.Empty(target.Lists);

            target.CancellationSource = null;
            target.CancelAfterMutation = null;
            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Import, 10, false, true),
                importedAt,
                CancellationToken.None);
            await service.RunAsync(
                new SqlToCosmosImportOptions(SqlToCosmosImportMode.Reconcile, 10, false, false),
                importedAt,
                CancellationToken.None);
            Assert.Single(target.Channels);
            Assert.Single(target.Lists);
        }

        private static SqlImportList CreateList(DateTimeOffset now)
        {
            return new SqlImportList(
                Guid.NewGuid(),
                Enumerable.Repeat((byte)7, 40).ToArray(),
                "List title",
                1.25m,
                now.AddDays(1),
                DateOnly.FromDateTime(now.UtcDateTime));
        }

        private static CosmosListDocument CreateListDocument(
            DateTimeOffset now,
            Guid? id = null)
        {
            return SqlToCosmosImportMapper.ToDocument(
                CreateList(now) with { Id = id ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee") },
                new[] { "channel-1" },
                now);
        }

        private static CosmosChannelDocument CreateChannelDocument(string id = "channel-1")
        {
            return SqlToCosmosImportMapper.ToDocument(
                new SqlImportChannel(
                    id,
                    $"https://example.test/{id}",
                    "Channel title",
                    "https://example.test/channel.jpg",
                    "playlist-id",
                    DateTimeOffset.UtcNow,
                    ChannelStatus.Active,
                    ChannelStatusReason.None,
                    null),
                Array.Empty<ChannelVideo>());
        }

        private sealed class FakeSource : ISqlToCosmosImportSource
        {
            private readonly IReadOnlyList<CosmosListDocument> _lists;
            private readonly IReadOnlyList<CosmosChannelDocument> _channels;

            public FakeSource(
                IReadOnlyList<CosmosListDocument> lists,
                IReadOnlyList<CosmosChannelDocument> channels)
            {
                _lists = lists;
                _channels = channels;
            }

            public IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
                DateTimeOffset importedAt,
                int batchSize,
                CancellationToken cancellationToken) => ToAsync(_lists, cancellationToken);

            public IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
                DateTimeOffset importedAt,
                int batchSize,
                CancellationToken cancellationToken) => ToAsync(_channels, cancellationToken);
        }

        private sealed class FakeTarget : ISqlToCosmosImportTarget
        {
            public FakeTarget(
                IEnumerable<CosmosListDocument> lists = null,
                IEnumerable<CosmosChannelDocument> channels = null)
            {
                Lists = (lists ?? Array.Empty<CosmosListDocument>())
                    .ToDictionary(value => value.Id, StringComparer.Ordinal);
                Channels = (channels ?? Array.Empty<CosmosChannelDocument>())
                    .ToDictionary(value => value.Id, StringComparer.Ordinal);
            }

            public Dictionary<string, CosmosListDocument> Lists { get; }
            public Dictionary<string, CosmosChannelDocument> Channels { get; }
            public int ShareLinkCount { get; set; }
            public int MutationCount { get; private set; }
            public int? ThrowAfterMutation { get; set; }
            public CancellationTokenSource CancellationSource { get; set; }
            public int? CancelAfterMutation { get; set; }
            public Action<CosmosListDocument> AfterListUpsert { get; set; }

            public IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
                int batchSize,
                CancellationToken cancellationToken) => ToAsync(Lists.Values.ToArray(), cancellationToken);

            public IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
                int batchSize,
                CancellationToken cancellationToken) => ToAsync(Channels.Values.ToArray(), cancellationToken);

            public Task<int> CountShareLinksAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(ShareLinkCount);
            }

            public Task UpsertListAsync(CosmosListDocument document, CancellationToken cancellationToken)
            {
                Lists[document.Id] = document;
                Mutate();
                AfterListUpsert?.Invoke(document);
                return Task.CompletedTask;
            }

            public Task UpsertChannelAsync(CosmosChannelDocument document, CancellationToken cancellationToken)
            {
                Channels[document.Id] = document;
                Mutate();
                return Task.CompletedTask;
            }

            private void Mutate()
            {
                MutationCount++;
                if (ThrowAfterMutation == MutationCount)
                {
                    throw new SimulatedInterruptionException();
                }
                if (CancelAfterMutation == MutationCount)
                {
                    CancellationSource?.Cancel();
                }
            }
        }

        private static async IAsyncEnumerable<T> ToAsync<T>(
            IEnumerable<T> values,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return value;
                await Task.Yield();
            }
        }

        private sealed class SimulatedInterruptionException : Exception
        {
        }
    }
}
