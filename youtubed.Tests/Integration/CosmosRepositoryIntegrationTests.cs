using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosRepositoryIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosRepositoryIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task CommonListPageUsesPointReadAndSingleReadManyWithOptionalRenewalWrite()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeAppClock { UtcNow = now };
            var listLogger = new RecordingLogger<CosmosListRepository>();
            var channelLogger = new RecordingLogger<CosmosChannelRepository>();
            var lists = new CosmosListRepository(_fixture.Context, clock, listLogger);
            var channels = new CosmosChannelRepository(_fixture.Context, channelLogger);
            var list = CreateList(now, DateOnly.FromDateTime(now.UtcDateTime));
            var channel = CreateChannel("UC-request-shape", "Request Shape", now);
            await lists.CreateAsync(list);
            await channels.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
            await lists.AddChannelAsync(list.Id, channel.Id);
            listLogger.Clear();

            var projection = await lists.GetAuthenticatedVideoProjectionAsync(
                list.Id,
                list.Token,
                now.AddDays(46),
                DateOnly.FromDateTime(now.UtcDateTime),
                100);

            Assert.NotNull(projection);
            Assert.Equal(
                new[] { "pointRead", "readMany" },
                listLogger.Messages.Select(message => message.Operation));
            Assert.All(listLogger.Messages, message => Assert.Equal(1, message.RequestCount));
            Assert.All(listLogger.Messages, message => Assert.True(message.RequestCharge > 0));
            Assert.DoesNotContain(listLogger.Messages, message =>
                message.Container != "lists" && message.Container != "channels");

            clock.UtcNow = now.AddDays(1);
            listLogger.Clear();
            await lists.GetAuthenticatedVideoProjectionAsync(
                list.Id,
                list.Token,
                clock.UtcNow.AddDays(46),
                DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
                100);
            Assert.Equal(
                new[] { "pointRead", "replace", "readMany" },
                listLogger.Messages.Select(message => message.Operation));
        }

        [CosmosFact]
        public async Task GenuineConcurrentMembershipAndRefreshWritesLeaveValidDocuments()
        {
            var now = DateTimeOffset.UtcNow;
            var clock = new FakeAppClock { UtcNow = now };
            var setupLists = new CosmosListRepository(
                _fixture.Context,
                clock,
                new RecordingLogger<CosmosListRepository>());
            var setupChannels = new CosmosChannelRepository(
                _fixture.Context,
                new RecordingLogger<CosmosChannelRepository>());
            var list = CreateList(now, DateOnly.FromDateTime(now.UtcDateTime));
            var channel = CreateChannel("UC-concurrent-old", "Old", now);
            await setupLists.CreateAsync(list);
            await setupChannels.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
            await setupLists.AddChannelAsync(list.Id, channel.Id);

            var requestLogger = new RecordingLogger<object>();
            var coordinatedClient = new CoordinatedCosmosRepositoryClient(
                new CosmosRepositoryClient(_fixture.Context, requestLogger));
            var firstLists = new CosmosListRepository(coordinatedClient, clock);
            var secondLists = new CosmosListRepository(coordinatedClient, clock);
            var firstChannels = new CosmosChannelRepository(coordinatedClient);
            var secondChannels = new CosmosChannelRepository(coordinatedClient);

            await Task.WhenAll(
                firstLists.AddChannelAsync(list.Id, "UC-concurrent-new"),
                secondLists.RemoveChannelAsync(list.Id, channel.Id));

            Assert.Single(requestLogger.Messages, message =>
                message.Container == "lists"
                && message.Operation == "replace"
                && message.Status == 412
                && message.RetryCount == 0);
            Assert.Single(requestLogger.Messages, message =>
                message.Container == "lists"
                && message.Operation == "replace"
                && message.Status == 200
                && message.RetryCount == 1);

            var membership = await firstLists.GetChannelProjectionAsync(list);
            Assert.Equal(new[] { "UC-concurrent-new" }, membership.ChannelIds);
            var missing = Assert.Single(membership.Channels);
            Assert.True(missing.IsMissing);
            requestLogger.Clear();

            var firstRefresh = CreateChannel(channel.Id, "First", now);
            firstRefresh.Videos = CreateVideos(channel.Id, now, 120);
            var secondRefresh = CreateChannel(channel.Id, "Second", now);
            secondRefresh.Videos = CreateVideos(channel.Id, now.AddSeconds(1), 120);
            await Task.WhenAll(
                firstChannels.SaveRefreshResultsAsync(
                    new[] { new ChannelRefreshResult { Channel = firstRefresh, VideosRefreshed = true } },
                    CancellationToken.None),
                secondChannels.SaveRefreshResultsAsync(
                    new[] { new ChannelRefreshResult { Channel = secondRefresh, VideosRefreshed = true } },
                    CancellationToken.None));

            Assert.Single(requestLogger.Messages, message =>
                message.Container == "channels"
                && message.Operation == "replace"
                && message.Status == 412
                && message.RetryCount == 0);
            Assert.Single(requestLogger.Messages, message =>
                message.Container == "channels"
                && message.Operation == "replace"
                && message.Status == 200
                && message.RetryCount == 1);

            var persisted = await firstChannels.GetByIdAsync(channel.Id);
            Assert.Contains(persisted.Title, new[] { "First", "Second" });
            Assert.Equal(ChannelStatus.Active, persisted.Status);
            Assert.Equal(100, persisted.Videos.Count);
            Assert.Equal(
                persisted.Videos
                    .OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .Select(video => video.VideoId),
                persisted.Videos.Select(video => video.VideoId));
        }

        private static SubscriptionList CreateList(DateTimeOffset now, DateOnly renewedOn)
        {
            return new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = Enumerable.Range(0, 40).Select(value => (byte)value).ToArray(),
                Title = "Cosmos repository list",
                PlaybackRate = 1.25m,
                ExpiredAfter = now.AddDays(45),
                ExpirationRenewedOn = renewedOn
            };
        }

        private static Channel CreateChannel(
            string id,
            string title,
            DateTimeOffset now)
        {
            return new Channel
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = title,
                Thumbnail = $"{id}.jpg",
                PlaylistId = id.Replace("UC", "UU", StringComparison.Ordinal),
                StaleAfter = now.AddHours(1),
                Status = ChannelStatus.Active
            };
        }

        private static IReadOnlyList<ChannelVideo> CreateVideos(
            string channelId,
            DateTimeOffset now,
            int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => new ChannelVideo
                {
                    ChannelId = channelId,
                    VideoId = $"video-{index:D3}",
                    Title = $"Video {index}",
                    Duration = TimeSpan.FromMinutes(2),
                    PublishedAt = now.AddMinutes(-index),
                    ThumbnailUrl = $"{index}.jpg"
                })
                .ToArray();
        }

        private sealed class CoordinatedCosmosRepositoryClient : ICosmosRepositoryClient
        {
            private readonly ICosmosRepositoryClient _inner;
            private readonly TwoPartyGate _listReplaceGate = new();
            private readonly TwoPartyGate _channelReplaceGate = new();

            public CoordinatedCosmosRepositoryClient(ICosmosRepositoryClient inner)
            {
                _inner = inner;
            }

            public Task<CosmosItem<CosmosListDocument>> CreateListAsync(
                CosmosListDocument document,
                int retryCount,
                CancellationToken cancellationToken) =>
                _inner.CreateListAsync(document, retryCount, cancellationToken);

            public Task<CosmosItem<CosmosListDocument>> ReadListAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken) =>
                _inner.ReadListAsync(id, retryCount, cancellationToken);

            public async Task<CosmosItem<CosmosListDocument>> ReplaceListAsync(
                CosmosListDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                if (retryCount == 0)
                {
                    await _listReplaceGate.ArriveAsync(cancellationToken);
                }

                return await _inner.ReplaceListAsync(
                    document,
                    etag,
                    retryCount,
                    cancellationToken);
            }

            public Task DeleteListAsync(string id, CancellationToken cancellationToken) =>
                _inner.DeleteListAsync(id, cancellationToken);

            public Task<CosmosItem<CosmosChannelDocument>> CreateChannelAsync(
                CosmosChannelDocument document,
                int retryCount,
                CancellationToken cancellationToken) =>
                _inner.CreateChannelAsync(document, retryCount, cancellationToken);

            public Task<CosmosItem<CosmosChannelDocument>> ReadChannelAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken) =>
                _inner.ReadChannelAsync(id, retryCount, cancellationToken);

            public Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
                IReadOnlyCollection<string> ids,
                CancellationToken cancellationToken) =>
                _inner.ReadChannelsAsync(ids, cancellationToken);

            public async Task<CosmosItem<CosmosChannelDocument>> ReplaceChannelAsync(
                CosmosChannelDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                if (retryCount == 0)
                {
                    await _channelReplaceGate.ArriveAsync(cancellationToken);
                }

                return await _inner.ReplaceChannelAsync(
                    document,
                    etag,
                    retryCount,
                    cancellationToken);
            }
        }

        private sealed class TwoPartyGate
        {
            private readonly TaskCompletionSource _bothArrived = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            private int _arrivalCount;

            public async Task ArriveAsync(CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _arrivalCount) == 2)
                {
                    _bothArrived.TrySetResult();
                }

                await _bothArrived.Task.WaitAsync(cancellationToken);
            }
        }

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            private readonly ConcurrentQueue<RequestMessage> _messages = new();

            public IReadOnlyList<RequestMessage> Messages => _messages.ToArray();

            public void Clear()
            {
                while (_messages.TryDequeue(out _))
                {
                }
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object>>;
                if (eventId.Id != 4100 || values == null)
                {
                    return;
                }

                var fields = values.ToDictionary(value => value.Key, value => value.Value);
                _messages.Enqueue(new RequestMessage(
                    (string)fields["Operation"],
                    (string)fields["Container"],
                    (int)fields["RequestCount"],
                    Convert.ToDouble(fields["RequestCharge"]),
                    (int)fields["Status"],
                    (int)fields["RetryCount"],
                    formatter(state, exception)));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private sealed record RequestMessage(
            string Operation,
            string Container,
            int RequestCount,
            double RequestCharge,
            int Status,
            int RetryCount,
            string Rendered);
    }
}
