using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosRepositoryTests
    {
        private static readonly DateTimeOffset Now =
            new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task AuthenticatedReadRejectsWrongTokenAndRenewsOnceBeforeOneReadMany()
        {
            var client = new FakeCosmosRepositoryClient();
            var clock = CreateClock();
            var repository = new CosmosListRepository(client, clock);
            var list = CreateList(expirationRenewedOn: clock.UtcToday.AddDays(-1));
            await repository.CreateAsync(list);

            Assert.Null(await repository.GetAuthenticatedVideoProjectionAsync(
                list.Id,
                new byte[] { 9, 9, 9 },
                Now.AddDays(46),
                clock.UtcToday,
                100));
            Assert.Equal(0, client.ListReplaceCount);
            Assert.Equal(0, client.ChannelReadManyCount);

            var projection = await repository.GetAuthenticatedVideoProjectionAsync(
                list.Id,
                list.Token,
                Now.AddDays(46),
                clock.UtcToday,
                100);

            Assert.NotNull(projection);
            Assert.Equal(Now.AddDays(46), projection.List.ExpiredAfter);
            Assert.Equal(clock.UtcToday, projection.List.ExpirationRenewedOn);
            Assert.Equal(1, client.ListReplaceCount);
            Assert.Equal(0, client.ChannelReadManyCount);

            await repository.GetAuthenticatedVideoProjectionAsync(
                list.Id,
                list.Token,
                Now.AddDays(47),
                clock.UtcToday,
                100);
            Assert.Equal(1, client.ListReplaceCount);
        }

        [Fact]
        public async Task MembershipIsSortedDistinctAndRejectsTheHundredAndFirstChannel()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);

            foreach (var index in Enumerable.Range(0, 100).Reverse())
            {
                await repository.AddChannelAsync(list.Id, $"UC-{index:D3}");
            }
            await repository.AddChannelAsync(list.Id, "UC-050");

            var document = client.GetList(list.Id);
            Assert.Equal(100, document.ChannelIds.Count);
            Assert.Equal(
                document.ChannelIds.OrderBy(id => id, StringComparer.Ordinal),
                document.ChannelIds);
            await Assert.ThrowsAsync<ListCapacityExceededException>(() =>
                repository.AddChannelAsync(list.Id, "UC-100"));
        }

        [Fact]
        public async Task ListMutationRetriesOneConflictAndLetsASecondConflictEscape()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);
            client.ListReplaceConflictsRemaining = 1;

            await repository.AddChannelAsync(list.Id, "UC-retried");

            Assert.Equal(new[] { "UC-retried" }, client.GetList(list.Id).ChannelIds);
            Assert.Equal(2, client.ListReplaceAttemptCount);
            Assert.Equal(2, client.ListReadCount);

            client.ListReplaceConflictsRemaining = 2;
            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.AddChannelAsync(list.Id, "UC-fails"));
            Assert.DoesNotContain("UC-fails", client.GetList(list.Id).ChannelIds);
        }

        [Fact]
        public async Task ReadModelsKeepMissingIdsAndBoundVideosDeterministically()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);
            await repository.AddChannelAsync(list.Id, "UC-present");
            await repository.AddChannelAsync(list.Id, "UC-missing");
            client.PutChannel(CreateChannelDocument(
                "UC-present",
                Enumerable.Range(0, 101)
                    .Select(index => new CosmosVideoDocument
                    {
                        Id = $"video-{index:D3}",
                        Title = $"Video {index}",
                        DurationTicks = TimeSpan.FromMinutes(1).Ticks,
                        PublishedAt = Now.AddMinutes(-(index % 3)),
                        Thumbnail = $"{index}.jpg"
                    })
                    .ToArray()));

            var projection = await repository.GetVideoProjectionAsync(list, 100);

            Assert.Equal(new[] { "UC-missing", "UC-present" }, projection.ChannelIds);
            var channel = Assert.Single(projection.Channels);
            Assert.Equal("UC-present", channel.Id);
            Assert.Equal(100, channel.Videos.Count);
            Assert.Equal(
                channel.Videos
                    .OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .Select(video => video.VideoId),
                channel.Videos.Select(video => video.VideoId));
            Assert.Equal(1, client.ChannelReadManyCount);

            var management = await repository.GetChannelProjectionAsync(list);
            var missing = management.Channels.Single(value => value.Id == "UC-missing");
            Assert.True(missing.IsMissing);
            Assert.Equal("Temporarily unavailable", missing.Title);
            Assert.Equal(ChannelStatus.Unavailable, missing.Status);
            Assert.Equal(
                "https://www.youtube.com/channel/UC-missing",
                missing.Url);

            await repository.RemoveChannelAsync(list.Id, missing.Id);
            var afterRemoval = await repository.GetChannelProjectionAsync(list);
            Assert.DoesNotContain("UC-missing", afterRemoval.ChannelIds);
            Assert.DoesNotContain(afterRemoval.Channels, value => value.Id == "UC-missing");
        }

        [Fact]
        public async Task ChannelRefreshBoundsVideosRetriesOnceAndPreservesVideosWhenNotRefreshed()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosChannelRepository(client);
            var channel = CreateChannel();
            await repository.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
            var refreshed = CreateChannel();
            refreshed.Title = "Refreshed";
            refreshed.Videos = Enumerable.Range(0, 105)
                .Select(index => new ChannelVideo
                {
                    ChannelId = channel.Id,
                    VideoId = $"video-{index:D3}",
                    Title = $"Video {index}",
                    Duration = TimeSpan.FromMinutes(1),
                    PublishedAt = Now.AddMinutes(-index),
                    ThumbnailUrl = $"{index}.jpg"
                })
                .ToArray();
            client.ChannelReplaceConflictsRemaining = 1;

            await repository.SaveRefreshResultAsync(
                new ChannelRefreshResult { Channel = refreshed, VideosRefreshed = true },
                CancellationToken.None);

            var saved = await repository.GetByIdAsync(channel.Id);
            Assert.Equal("Refreshed", saved.Title);
            Assert.Equal(100, saved.Videos.Count);
            Assert.Equal(2, client.ChannelReplaceAttemptCount);

            var metadataOnly = CreateChannel();
            metadataOnly.Title = "Metadata only";
            metadataOnly.Videos = Array.Empty<ChannelVideo>();
            await repository.SaveRefreshResultAsync(
                new ChannelRefreshResult { Channel = metadataOnly, VideosRefreshed = false },
                CancellationToken.None);
            Assert.Equal(100, (await repository.GetByIdAsync(channel.Id)).Videos.Count);

            client.ChannelReplaceConflictsRemaining = 2;
            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.SaveRefreshResultAsync(
                    new ChannelRefreshResult { Channel = refreshed },
                    CancellationToken.None));
        }

        [Fact]
        public void TelemetryIsBoundedAndContainsNoIdentifiersDiagnosticsOrSecrets()
        {
            var logger = new RecordingLogger();
            const string secret = "secret-token-value";
            const string resourceId = "private-resource-id";

            CosmosRepositoryClient.WriteTelemetry(
                logger,
                "pointRead",
                CosmosContainerNames.Lists,
                HttpStatusCode.OK,
                1.25,
                TimeSpan.FromMilliseconds(12),
                retryCount: 1);

            var message = Assert.Single(logger.Messages);
            Assert.Contains("RequestCount=1", message);
            Assert.Contains("RequestCharge=1.25", message);
            Assert.Contains("Status=200", message);
            Assert.Contains("RetryCount=1", message);
            Assert.DoesNotContain(secret, message);
            Assert.DoesNotContain(resourceId, message);
            Assert.DoesNotContain("diagnostic", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dbs/", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExpirationPurgerLeavesTtlAndChannelRetentionToCosmosPolicy()
        {
            var purger = new CosmosExpirationPurger();

            Assert.Equal(0, await purger.PurgeExpiredListsAsync(CancellationToken.None));
            Assert.Equal(0, await purger.PurgeExpiredShareLinksAsync(CancellationToken.None));
            Assert.Equal(0, await purger.PurgeExpiredChannelsAsync(CancellationToken.None));
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                purger.PurgeExpiredListsAsync(new CancellationToken(canceled: true)));
        }

        private static FakeAppClock CreateClock()
        {
            return new FakeAppClock { UtcNow = Now };
        }

        private static SubscriptionList CreateList(DateOnly? expirationRenewedOn = null)
        {
            return new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = new byte[] { 1, 2, 3 },
                Title = "List",
                PlaybackRate = 1.25m,
                ExpiredAfter = Now.AddDays(45),
                ExpirationRenewedOn = expirationRenewedOn
            };
        }

        private static Channel CreateChannel()
        {
            return new Channel
            {
                Id = "UC-channel",
                Url = "https://www.youtube.com/channel/UC-channel",
                Title = "Channel",
                Thumbnail = "channel.jpg",
                PlaylistId = "UU-channel",
                StaleAfter = Now.AddHours(1)
            };
        }

        private static CosmosChannelDocument CreateChannelDocument(
            string id,
            IReadOnlyList<CosmosVideoDocument> videos)
        {
            return new CosmosChannelDocument
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = id,
                Thumbnail = $"{id}.jpg",
                PlaylistId = id.Replace("UC", "UU", StringComparison.Ordinal),
                StaleAfter = Now.AddHours(1),
                Status = ChannelStatus.Active.ToString(),
                Videos = videos
            };
        }

        private sealed class RecordingLogger : ILogger
        {
            public List<string> Messages { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                Messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private sealed class FakeCosmosRepositoryClient : ICosmosRepositoryClient
        {
            private readonly Dictionary<string, CosmosListDocument> _lists = new(StringComparer.Ordinal);
            private readonly Dictionary<string, CosmosChannelDocument> _channels = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

            public int ListReadCount { get; private set; }
            public int ListReplaceCount { get; private set; }
            public int ListReplaceAttemptCount { get; private set; }
            public int ListReplaceConflictsRemaining { get; set; }
            public int ChannelReadManyCount { get; private set; }
            public int ChannelReplaceAttemptCount { get; private set; }
            public int ChannelReplaceConflictsRemaining { get; set; }

            public CosmosListDocument GetList(Guid id) => Clone(_lists[id.ToString("D")]);

            public void PutChannel(CosmosChannelDocument document)
            {
                _channels[document.Id] = Clone(document);
                _versions[document.Id] = 1;
            }

            public Task<CosmosItem<CosmosListDocument>> CreateListAsync(
                CosmosListDocument document,
                int retryCount,
                CancellationToken cancellationToken)
            {
                _lists.Add(document.Id, Clone(document));
                _versions[document.Id] = 1;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task<CosmosItem<CosmosListDocument>> ReadListAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ListReadCount++;
                return Task.FromResult(_lists.TryGetValue(id, out var document)
                    ? Item(Clone(document), id)
                    : null);
            }

            public Task<CosmosItem<CosmosListDocument>> ReplaceListAsync(
                CosmosListDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ListReplaceAttemptCount++;
                if (ListReplaceConflictsRemaining-- > 0)
                {
                    throw Conflict();
                }

                Assert.Equal(CurrentEtag(document.Id), etag);
                _lists[document.Id] = Clone(document);
                _versions[document.Id]++;
                ListReplaceCount++;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task DeleteListAsync(string id, CancellationToken cancellationToken)
            {
                _lists.Remove(id);
                return Task.CompletedTask;
            }

            public Task<CosmosItem<CosmosChannelDocument>> CreateChannelAsync(
                CosmosChannelDocument document,
                int retryCount,
                CancellationToken cancellationToken)
            {
                _channels.Add(document.Id, Clone(document));
                _versions[document.Id] = 1;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task<CosmosItem<CosmosChannelDocument>> ReadChannelAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_channels.TryGetValue(id, out var document)
                    ? Item(Clone(document), id)
                    : null);
            }

            public Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
                IReadOnlyCollection<string> ids,
                CancellationToken cancellationToken)
            {
                ChannelReadManyCount++;
                IReadOnlyList<CosmosChannelDocument> result = ids
                    .Where(_channels.ContainsKey)
                    .Select(id => Clone(_channels[id]))
                    .ToArray();
                return Task.FromResult(result);
            }

            public Task<CosmosItem<CosmosChannelDocument>> ReplaceChannelAsync(
                CosmosChannelDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ChannelReplaceAttemptCount++;
                if (ChannelReplaceConflictsRemaining-- > 0)
                {
                    throw Conflict();
                }

                Assert.Equal(CurrentEtag(document.Id), etag);
                _channels[document.Id] = Clone(document);
                _versions[document.Id]++;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            private CosmosItem<T> Item<T>(T resource, string id)
            {
                return new CosmosItem<T>(resource, CurrentEtag(id));
            }

            private string CurrentEtag(string id) => $"etag-{_versions[id]}";

            private static T Clone<T>(T value)
            {
                var stream = CosmosSystemTextJsonSerializer.Instance.ToStream(value);
                return CosmosSystemTextJsonSerializer.Instance.FromStream<T>(stream);
            }

            private static CosmosException Conflict()
            {
                return new CosmosException(
                    "Concurrency conflict.",
                    HttpStatusCode.PreconditionFailed,
                    subStatusCode: 0,
                    activityId: string.Empty,
                    requestCharge: 0);
            }
        }
    }
}
