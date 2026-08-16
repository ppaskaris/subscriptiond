using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
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
            var listLogger = new CosmosRequestRecorder<CosmosListRepository>();
            var channelLogger = new CosmosRequestRecorder<CosmosChannelRepository>();
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
                listLogger.Records.Select(message => message.Operation));
            Assert.All(listLogger.Records, message => Assert.Equal(1, message.RequestCount));
            Assert.All(listLogger.Records, message => Assert.True(message.RequestCharge > 0));
            Assert.DoesNotContain(listLogger.Records, message =>
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
                listLogger.Records.Select(message => message.Operation));
        }

        [CosmosFact]
        public async Task GenuineConcurrentMembershipAndRefreshWritesLeaveValidDocuments()
        {
            var now = DateTimeOffset.UtcNow;
            var clock = new FakeAppClock { UtcNow = now };
            var setupLists = new CosmosListRepository(
                _fixture.Context,
                clock,
                new CosmosRequestRecorder<CosmosListRepository>());
            var setupChannels = new CosmosChannelRepository(
                _fixture.Context,
                new CosmosRequestRecorder<CosmosChannelRepository>());
            var list = CreateList(now, DateOnly.FromDateTime(now.UtcDateTime));
            var channel = CreateChannel("UC-concurrent-old", "Old", now);
            await setupLists.CreateAsync(list);
            await setupChannels.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
            await setupLists.AddChannelAsync(list.Id, channel.Id);

            var requestLogger = new CosmosRequestRecorder<object>();
            var coordinatedClient = new CoordinatedCosmosRepositoryClient(
                new CosmosRepositoryClient(_fixture.Context, requestLogger));
            var firstLists = new CosmosListRepository(coordinatedClient, clock);
            var secondLists = new CosmosListRepository(coordinatedClient, clock);
            var firstChannels = new CosmosChannelRepository(coordinatedClient);
            var secondChannels = new CosmosChannelRepository(coordinatedClient);

            await Task.WhenAll(
                firstLists.AddChannelAsync(list.Id, "UC-concurrent-new"),
                secondLists.RemoveChannelAsync(list.Id, channel.Id));

            Assert.Single(requestLogger.Records, message =>
                message.Container == "lists"
                && message.Operation == "replace"
                && message.Status == 412
                && message.RetryCount == 0);
            Assert.Single(requestLogger.Records, message =>
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
                firstChannels.SaveRefreshResultAsync(
                    new ChannelRefreshResult { Channel = firstRefresh, VideosRefreshed = true },
                    CancellationToken.None),
                secondChannels.SaveRefreshResultAsync(
                    new ChannelRefreshResult { Channel = secondRefresh, VideosRefreshed = true },
                    CancellationToken.None));

            Assert.Single(requestLogger.Records, message =>
                message.Container == "channels"
                && message.Operation == "replace"
                && message.Status == 412
                && message.RetryCount == 0);
            Assert.Single(requestLogger.Records, message =>
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

        [CosmosFact]
        public async Task SdkRetriesAnInjected429OnceThenSurfacesExhaustionWithoutLeakingDetails()
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeAppClock { UtcNow = now };
            var list = CreateList(now, DateOnly.FromDateTime(now.UtcDateTime));
            var document = CosmosDocumentMapper.ToDocument(
                list,
                Array.Empty<string>(),
                now);
            await _fixture.Context.Lists.CreateItemAsync(
                document,
                new PartitionKey(document.Id));

            var handler = new InjectedThrottleHandler(document.Id)
            {
                InnerHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }
            };
            using var httpClient = new HttpClient(handler);
            var clientOptions = CosmosClientFactory.CreateClientOptions();
            clientOptions.HttpClientFactory = () => httpClient;
            clientOptions.MaxRetryAttemptsOnRateLimitedRequests = 1;
            clientOptions.MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(1);
            var emulator = CosmosEmulatorOptions.FromEnvironment();
            using var client = new CosmosClient(emulator.ConnectionString, clientOptions);
            var context = new CosmosPersistenceContext(
                client,
                new CosmosOptions { DatabaseName = _fixture.DatabaseName });
            var logger = new CosmosRequestRecorder<CosmosListRepository>();
            var repository = new CosmosListRepository(context, clock, logger);

            try
            {
                var exception = await Assert.ThrowsAsync<CosmosException>(() =>
                    repository.GetAsync(list.Id));

                Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
                Assert.Equal(2, handler.InjectedResponseCount);
                var request = Assert.Single(logger.Records);
                Assert.Equal("pointRead", request.Operation);
                Assert.Equal((int)HttpStatusCode.TooManyRequests, request.Status);
                Assert.Equal(0, request.RetryCount);
                Assert.DoesNotContain(document.Id, request.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("diagnostic", request.Rendered, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("dbs/", request.Rendered, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await _fixture.Context.Lists.DeleteItemAsync<CosmosListDocument>(
                    document.Id,
                    new PartitionKey(document.Id));
            }
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

        private sealed class InjectedThrottleHandler : DelegatingHandler
        {
            private readonly string _targetId;
            private int _injectedResponseCount;

            public InjectedThrottleHandler(string targetId)
            {
                _targetId = targetId;
            }

            public int InjectedResponseCount => _injectedResponseCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Get
                    && request.RequestUri.AbsolutePath.Contains(
                        $"/docs/{_targetId}",
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _injectedResponseCount);
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        RequestMessage = request,
                        Content = new StringContent(
                            "{\"code\":\"TooManyRequests\",\"message\":\"Injected throttle.\"}")
                    };
                    response.Headers.TryAddWithoutValidation("x-ms-activity-id", Guid.Empty.ToString());
                    response.Headers.TryAddWithoutValidation("x-ms-request-charge", "0");
                    response.Headers.TryAddWithoutValidation("x-ms-retry-after-ms", "1");
                    response.Headers.TryAddWithoutValidation("x-ms-substatus", "0");
                    return Task.FromResult(response);
                }

                return base.SendAsync(request, cancellationToken);
            }
        }

    }
}
