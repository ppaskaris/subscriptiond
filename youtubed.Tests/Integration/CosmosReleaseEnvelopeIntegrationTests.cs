using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Cosmos;
using Xunit;
using Xunit.Abstractions;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosReleaseEnvelopeIntegrationTests
    {
        private const int ItemSafetyCeilingBytes = 512 * 1024;
        private const double AvailableRuPerSecond = 700d;

        private static readonly WorkloadShape[] Shapes =
        {
            new("small", ChannelCount: 1, VideosPerChannel: 3),
            new("representative", ChannelCount: 10, VideosPerChannel: 20),
            new(
                "maximum",
                CosmosDocumentMapper.MaximumChannelIds,
                CosmosDocumentMapper.MaximumVideos)
        };

        private readonly CosmosTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public CosmosReleaseEnvelopeIntegrationTests(
            CosmosTestFixture fixture,
            ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [CosmosFact]
        public async Task SupportedDatasetShapesStayWithinTheMeasuredReleaseEnvelope()
        {
            var evidence = new List<ShapeEvidence>();
            foreach (var shape in Shapes)
            {
                evidence.Add(await MeasureShapeAsync(shape));
            }

            var representative = Assert.Single(
                evidence,
                item => item.Shape.Name == "representative");
            var representativeRuPerMinute =
                (representative.SameDayRender.RequestCharge * 60)
                + representative.RenewalRender.RequestCharge
                + representative.CacheMissAdd.RequestCharge
                + representative.CacheHitAdd.RequestCharge
                + (representative.Remove.RequestCharge * 2)
                + (representative.Refresh.RequestCharge * 10)
                + (representative.ShareCycleRequestCharge * 2);
            var representativeRuPerSecond = representativeRuPerMinute / 60d;

            _output.WriteLine(
                "Representative traffic envelope: 60 same-day renders, 1 renewal render, " +
                "1 cache-miss add, 1 cache-hit add, 2 removes, 10 channel refreshes, " +
                "and 2 complete share cycles per minute.");
            _output.WriteLine(
                $"Measured demand={Format(representativeRuPerSecond)} RU/s; " +
                $"limit={Format(AvailableRuPerSecond)} RU/s (30% reserve on 1,000 RU/s).");

            Assert.True(
                representativeRuPerSecond <= AvailableRuPerSecond,
                $"Representative demand was {Format(representativeRuPerSecond)} RU/s; " +
                $"the release envelope allows at most {Format(AvailableRuPerSecond)} RU/s.");
        }

        private async Task<ShapeEvidence> MeasureShapeAsync(WorkloadShape shape)
        {
            var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeAppClock { UtcNow = now };
            var listLogger = new CosmosRequestRecorder<object>();
            var shareLogger = new CosmosRequestRecorder<CosmosShareLinkRepository>();
            var repositoryClient = new CosmosRepositoryClient(_fixture.Context, listLogger);
            var lists = new CosmosListRepository(repositoryClient, clock);
            var channels = new CosmosChannelRepository(repositoryClient);
            var service = new ListService(
                lists,
                channels,
                clock,
                new ChannelRefreshQueue());
            var shares = new CosmosShareLinkRepository(_fixture.Context, clock, shareLogger);
            var scope = Guid.NewGuid().ToString("N");
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = token,
                Title = $"Cosmos release envelope {shape.Name}",
                PlaybackRate = 1.25m,
                ExpiredAfter = now.AddDays(45),
                ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime)
            };
            var channelDocuments = Enumerable.Range(0, shape.ChannelCount)
                .Select(index => CreateChannel(scope, index, shape.VideosPerChannel, now))
                .ToArray();
            var channelIds = channelDocuments.Select(channel => channel.Id).ToArray();

            try
            {
                await lists.CreateAsync(list);
                foreach (var channel in channelDocuments)
                {
                    await channels.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
                    await lists.AddChannelAsync(list.Id, channel.Id);
                }

                list.ChannelIds = channelIds;
                var listSize = CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(
                    CosmosDocumentMapper.ToDocument(list, clock.UtcNow));
                var channelSize = channelDocuments.Max(channel =>
                    CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(
                        CosmosDocumentMapper.ToDocument(channel)));
                Assert.InRange(listSize, 1, ItemSafetyCeilingBytes);
                Assert.InRange(channelSize, 1, ItemSafetyCeilingBytes);

                var sameDayRender = await MeasureAsync(
                    "same-day render",
                    listLogger,
                    () => service.GetAuthenticatedListViewAsync(
                        list.Id,
                        WebEncoders.Base64UrlEncode(token)));
                AssertRequestShape(sameDayRender, "pointRead", "readMany");

                clock.UtcNow = now.AddDays(1);
                var renewalRender = await MeasureAsync(
                    "renewal render",
                    listLogger,
                    () => service.GetAuthenticatedListViewAsync(
                        list.Id,
                        WebEncoders.Base64UrlEncode(token)));
                AssertRequestShape(renewalRender, "pointRead", "replace", "readMany");

                var spare = CreateChannel(scope, shape.ChannelCount, videoCount: 0, now);
                await lists.RemoveChannelAsync(list.Id, channelIds[^1]);
                listLogger.Clear();
                await channels.SaveDiscoveredChannelAsync(spare, spare.StaleAfter);
                await lists.AddChannelAsync(list.Id, spare.Id);
                var cacheMissAdd = new OperationMeasurement(
                    "cache-miss add",
                    listLogger.Records.ToArray());
                AssertRequestShape(
                    cacheMissAdd,
                    new[] { 404, 201, 200, 200 },
                    "pointRead",
                    "create",
                    "pointRead",
                    "replace");
                var remove = await MeasureAsync(
                    "remove channel",
                    listLogger,
                    () => lists.RemoveChannelAsync(list.Id, spare.Id));
                AssertRequestShape(remove, "pointRead", "replace");
                listLogger.Clear();
                Assert.NotNull(await channels.GetByIdAsync(spare.Id));
                await lists.AddChannelAsync(list.Id, spare.Id);
                var cacheHitAdd = new OperationMeasurement(
                    "cache-hit add",
                    listLogger.Records.ToArray());
                AssertRequestShape(cacheHitAdd, "pointRead", "pointRead", "replace");

                var refreshed = CreateChannel(scope, 0, shape.VideosPerChannel, now.AddMinutes(1));
                refreshed.Title = "Refreshed channel";
                var refresh = await MeasureAsync(
                    "channel refresh",
                    listLogger,
                    () => channels.SaveRefreshResultAsync(
                        new ChannelRefreshResult
                        {
                            Channel = refreshed,
                            VideosRefreshed = true
                        },
                        CancellationToken.None));
                AssertRequestShape(refresh, "pointRead", "replace");

                var share = new ShareLink
                {
                    Password = $"release-{scope}",
                    ListId = list.Id,
                    CreatedAt = clock.UtcNow,
                    ExpiresAfter = clock.UtcNow.AddHours(1)
                };
                var shareCreate = await MeasureAsync(
                    "share create",
                    shareLogger,
                    async () => Assert.True(await shares.TryCreateAsync(share)));
                AssertRequestShape(shareCreate, "create");
                var shareList = await MeasureAsync(
                    "share list",
                    shareLogger,
                    async () => Assert.Single(await shares.GetByListAsync(list.Id)));
                AssertRequestShape(shareList, "query");
                var shareConsume = await MeasureAsync(
                    "share consume",
                    shareLogger,
                    async () => Assert.NotNull(await shares.ConsumeAsync(share.Password, clock.UtcNow)));
                AssertRequestShape(shareConsume, "pointRead", "pointRead", "replace");
                var shareDelete = await MeasureAsync(
                    "share delete",
                    shareLogger,
                    () => shares.DeleteAsync(list.Id, share.Password));
                AssertRequestShape(shareDelete, "pointRead", "delete");

                var shapeEvidence = new ShapeEvidence(
                    shape,
                    listSize,
                    channelSize,
                    sameDayRender,
                    renewalRender,
                    cacheMissAdd,
                    cacheHitAdd,
                    remove,
                    refresh,
                    shareCreate,
                    shareList,
                    shareConsume,
                    shareDelete);
                WriteEvidence(shapeEvidence);
                AssertSecretSafe(shapeEvidence, token, share.Password);
                return shapeEvidence;
            }
            finally
            {
                await DeleteIfPresentAsync<CosmosListDocument>(
                    _fixture.Context.Lists,
                    list.Id.ToString("D"));
                foreach (var channelId in channelIds.Append(
                    CreateChannelId(scope, shape.ChannelCount)))
                {
                    await DeleteIfPresentAsync<CosmosChannelDocument>(
                        _fixture.Context.Channels,
                        channelId);
                }

                await DeleteIfPresentAsync<CosmosShareLinkDocument>(
                    _fixture.Context.ShareLinks,
                    $"release-{scope}");
            }
        }

        private static Channel CreateChannel(
            string scope,
            int channelIndex,
            int videoCount,
            DateTimeOffset now)
        {
            var channelId = CreateChannelId(scope, channelIndex);
            return new Channel
            {
                Id = channelId,
                Url = $"https://www.youtube.com/channel/{channelId}",
                Title = $"Channel {channelIndex + 1}",
                Thumbnail = $"https://i.ytimg.com/channel/{channelId}/default.jpg",
                PlaylistId = $"UU{scope[..19]}{channelIndex:D3}",
                StaleAfter = now.AddHours(1),
                Status = ChannelStatus.Active,
                Videos = Enumerable.Range(0, videoCount)
                    .Select(videoIndex => new ChannelVideo
                    {
                        ChannelId = channelId,
                        VideoId = $"{channelIndex:D3}{videoIndex:D3}video",
                        Title = $"Video {videoIndex + 1} from channel {channelIndex + 1}",
                        Duration = TimeSpan.FromMinutes(3),
                        PublishedAt = now.AddMinutes(-(channelIndex * videoCount + videoIndex)),
                        ThumbnailUrl =
                            $"https://i.ytimg.com/vi/{channelIndex:D3}{videoIndex:D3}video/mqdefault.jpg"
                    })
                    .ToArray()
            };
        }

        private static string CreateChannelId(string scope, int channelIndex) =>
            $"UC{scope[..19]}{channelIndex:D3}";

        private static async Task<OperationMeasurement> MeasureAsync<T>(
            string name,
            CosmosRequestRecorder<T> recorder,
            Func<Task> operation)
        {
            recorder.Clear();
            await operation();
            return new OperationMeasurement(name, recorder.Records);
        }

        private static void AssertRequestShape(
            OperationMeasurement measurement,
            params string[] expectedOperations)
        {
            AssertRequestShape(measurement, null, expectedOperations);
        }

        private static void AssertRequestShape(
            OperationMeasurement measurement,
            IReadOnlyList<int> expectedStatuses,
            params string[] expectedOperations)
        {
            Assert.Equal(
                expectedOperations,
                measurement.Requests.Select(request => request.Operation));
            for (var index = 0; index < measurement.Requests.Count; index++)
            {
                var request = measurement.Requests[index];
                Assert.Equal(1, request.RequestCount);
                Assert.InRange(request.RequestCharge, double.Epsilon, AvailableRuPerSecond);
                Assert.InRange(request.ElapsedMilliseconds, 0d, 60_000d);
                if (expectedStatuses == null)
                {
                    Assert.InRange(request.Status, 200, 299);
                }
                else
                {
                    Assert.Equal(expectedStatuses[index], request.Status);
                }
            }
            Assert.True(
                measurement.RequestCharge <= AvailableRuPerSecond,
                $"{measurement.Name} consumed {Format(measurement.RequestCharge)} RU; " +
                $"the release envelope allows {Format(AvailableRuPerSecond)} RU per operation.");
        }

        private static void AssertSecretSafe(
            ShapeEvidence evidence,
            byte[] token,
            string sharePassword)
        {
            var encodedToken = Convert.ToBase64String(token);
            foreach (var request in evidence.AllRequests)
            {
                Assert.DoesNotContain(encodedToken, request.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(sharePassword, request.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("AccountKey=", request.Rendered, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("AccountEndpoint=", request.Rendered, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("diagnostics", request.Rendered, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void WriteEvidence(ShapeEvidence evidence)
        {
            _output.WriteLine(
                $"Shape={evidence.Shape.Name}; channels={evidence.Shape.ChannelCount}; " +
                $"videos/channel={evidence.Shape.VideosPerChannel}; listBytes={evidence.ListBytes}; " +
                $"channelBytes={evidence.ChannelBytes}.");
            foreach (var operation in evidence.Operations)
            {
                _output.WriteLine(
                    $"  {operation.Name}: requests={operation.RequestCount}; " +
                    $"RU={Format(operation.RequestCharge)}; " +
                    $"latencyMs={Format(operation.ElapsedMilliseconds)}; " +
                    $"shape={string.Join(",", operation.Requests.Select(request =>
                        $"{request.Container}/{request.Operation}"))}.");
            }
        }

        private static async Task DeleteIfPresentAsync<T>(Container container, string id)
        {
            try
            {
                await container.DeleteItemAsync<T>(id, new PartitionKey(id));
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        private static string Format(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private sealed record WorkloadShape(
            string Name,
            int ChannelCount,
            int VideosPerChannel);

        private sealed record OperationMeasurement(
            string Name,
            IReadOnlyList<CosmosRequestRecord> Requests)
        {
            public int RequestCount => Requests.Sum(request => request.RequestCount);
            public double RequestCharge => Requests.Sum(request => request.RequestCharge);
            public double ElapsedMilliseconds => Requests.Sum(request => request.ElapsedMilliseconds);
        }

        private sealed record ShapeEvidence(
            WorkloadShape Shape,
            int ListBytes,
            int ChannelBytes,
            OperationMeasurement SameDayRender,
            OperationMeasurement RenewalRender,
            OperationMeasurement CacheMissAdd,
            OperationMeasurement CacheHitAdd,
            OperationMeasurement Remove,
            OperationMeasurement Refresh,
            OperationMeasurement ShareCreate,
            OperationMeasurement ShareList,
            OperationMeasurement ShareConsume,
            OperationMeasurement ShareDelete)
        {
            public IReadOnlyList<OperationMeasurement> Operations => new[]
            {
                SameDayRender,
                RenewalRender,
                CacheMissAdd,
                CacheHitAdd,
                Remove,
                Refresh,
                ShareCreate,
                ShareList,
                ShareConsume,
                ShareDelete
            };

            public IEnumerable<CosmosRequestRecord> AllRequests =>
                Operations.SelectMany(operation => operation.Requests);

            public double ShareCycleRequestCharge =>
                ShareCreate.RequestCharge
                + ShareList.RequestCharge
                + ShareConsume.RequestCharge
                + ShareDelete.RequestCharge;
        }
    }
}
