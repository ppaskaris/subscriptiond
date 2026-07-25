using Microsoft.Azure.Cosmos;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosListProjectionSizingIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public CosmosListProjectionSizingIntegrationTests(
            CosmosTestFixture fixture,
            ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [CosmosFact]
        public async Task AddAndProjectionReplacementAtMaximumCardinalityStayWithinSizeAndRuBudgets()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeAppClock { UtcNow = now };
            var listId = Guid.NewGuid();
            var listDocument = new CosmosListDocument
            {
                Id = listId.ToString("D"),
                Token = new byte[40],
                Title = "Maximum supported projection cardinality",
                PlaybackRate = 1,
                ExpiredAfter = now.AddDays(45),
                Ttl = (int)TimeSpan.FromDays(45).TotalSeconds,
                Channels = Enumerable.Range(0, CosmosListProjectionPolicy.MaxChannelsPerList - 1)
                    .Select(index => CreateProjectedChannel(CreateChannelId(index), now))
                    .ToArray()
            };
            var addedChannel = CreateChannel(
                CreateChannelId(CosmosListProjectionPolicy.MaxChannelsPerList - 1),
                now,
                listId);
            var lists = _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
            var channels = _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName);
            await lists.CreateItemAsync(listDocument, new PartitionKey(listDocument.Id));
            var channelDocument = CosmosDocumentMapper.ToChannelDocument(
                addedChannel,
                now,
                Constants.ChannelOrphanRetention);
            channelDocument.SubscribedListIds = Array.Empty<string>();
            channelDocument.SubscriptionCount = 0;
            channelDocument.OrphanedAfter = now;
            channelDocument.Ttl = (int)Constants.ChannelOrphanRetention.TotalSeconds;
            await channels.CreateItemAsync(channelDocument, new PartitionKey(channelDocument.Id));

            var listRepository = new CosmosListRepository(lists, channels, clock);
            await listRepository.AddChannelAsync(listId, addedChannel.Id);

            var afterAdd = await lists.ReadItemAsync<CosmosListDocument>(
                listDocument.Id,
                new PartitionKey(listDocument.Id));
            Assert.Equal(
                CosmosListProjectionPolicy.MaxChannelsPerList,
                afterAdd.Resource.Channels.Count);
            Assert.Equal(
                CosmosListProjectionPolicy.MaxProjectedVideosPerList,
                afterAdd.Resource.Channels.Sum(channel => channel.Videos.Count));
            Assert.True(
                CosmosListProjectionPolicy.GetSerializedSizeBytes(afterAdd.Resource)
                    < CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes);
            Assert.True(
                CosmosListProjectionPolicy.GetSerializedSizeBytes(afterAdd.Resource)
                    >= 1_700_000);
            Assert.InRange(
                afterAdd.RequestCharge,
                double.Epsilon,
                CosmosListProjectionPolicy.PointReadRuBudget);

            addedChannel.Title = new string('界', 250);
            addedChannel.Videos = Enumerable.Range(0, CosmosListProjectionPolicy.PerChannelMinimum)
                .Select(index => CreateVideo(
                    addedChannel.Id,
                    CreateVideoId(99, index),
                    now.AddMinutes(-index)))
                .ToArray();
            var projectionRepository = new CosmosListProjectionRepository(lists, channels, clock);
            await projectionRepository.UpdateProjectedChannelsAsync(
                new[] { addedChannel },
                CancellationToken.None);

            var afterProjection = await lists.ReadItemAsync<CosmosListDocument>(
                listDocument.Id,
                new PartitionKey(listDocument.Id));
            Assert.Equal(
                new string('界', 250),
                afterProjection.Resource.Channels.Single(channel => channel.Id == addedChannel.Id).Title);
            Assert.True(
                CosmosListProjectionPolicy.GetSerializedSizeBytes(afterProjection.Resource)
                    < CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes);
            var representativeWrite = await lists.ReplaceItemAsync(
                afterProjection.Resource,
                afterProjection.Resource.Id,
                new PartitionKey(afterProjection.Resource.Id),
                new ItemRequestOptions { IfMatchEtag = afterProjection.ETag });

            _output.WriteLine(
                $"Near-ceiling document serialized to " +
                $"{CosmosListProjectionPolicy.GetSerializedSizeBytes(afterProjection.Resource)} bytes; " +
                $"maximum-cardinality list point read consumed {afterProjection.RequestCharge:F2} RU; " +
                $"representative projection replacement consumed {representativeWrite.RequestCharge:F2} RU.");
            Assert.InRange(
                afterProjection.RequestCharge,
                double.Epsilon,
                CosmosListProjectionPolicy.PointReadRuBudget);
            Assert.InRange(
                representativeWrite.RequestCharge,
                double.Epsilon,
                CosmosListProjectionPolicy.ProjectionWriteRuBudget);
        }

        private static CosmosProjectedChannelDocument CreateProjectedChannel(
            string id,
            DateTimeOffset now)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = id,
                Url = PadToLength($"https://www.youtube.com/channel/{id}", 250, 'u'),
                Title = new string('界', 250),
                Thumbnail = PadToLength("https://i.ytimg.com/", 2000, 't'),
                StaleAfter = now,
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString(),
                Videos = Enumerable.Range(0, CosmosListProjectionPolicy.PerChannelMinimum)
                    .Select(index => new CosmosVideoDocument
                    {
                        Id = CreateVideoId(int.Parse(id[^3..]), index),
                        Title = new string('界', 100),
                        DurationTicks = TimeSpan.FromMinutes(5).Ticks,
                        PublishedAt = now.AddDays(-10).AddMinutes(-index),
                        Thumbnail = PadToLength("https://i.ytimg.com/", 2000, 't')
                    })
                    .ToArray()
            };
        }

        private static Channel CreateChannel(
            string id,
            DateTimeOffset now,
            Guid listId)
        {
            return new Channel
            {
                Id = id,
                Url = PadToLength($"https://www.youtube.com/channel/{id}", 250, 'u'),
                Title = new string('界', 250),
                Thumbnail = PadToLength("https://i.ytimg.com/", 2000, 't'),
                PlaylistId = new string('p', 50),
                StaleAfter = now,
                Status = ChannelStatus.Active,
                StatusReason = ChannelStatusReason.None,
                SubscribedListIds = new[] { listId },
                SubscriptionCount = 1,
                Videos = Enumerable.Range(0, CosmosListProjectionPolicy.PerChannelMinimum)
                    .Select(index => CreateVideo(
                        id,
                        $"{id}-video-{index:D2}",
                        now.AddDays(-10).AddMinutes(-index)))
                    .ToArray()
            };
        }

        private static ChannelVideo CreateVideo(
            string channelId,
            string videoId,
            DateTimeOffset publishedAt)
        {
            return new ChannelVideo
            {
                ChannelId = channelId,
                VideoId = videoId,
                Title = new string('界', 100),
                Duration = TimeSpan.FromMinutes(5),
                PublishedAt = publishedAt,
                ThumbnailUrl = PadToLength("https://i.ytimg.com/", 2000, 't')
            };
        }

        private static string CreateChannelId(int index)
        {
            return new string('c', 47) + index.ToString("D3");
        }

        private static string CreateVideoId(int channelIndex, int videoIndex)
        {
            return channelIndex.ToString("D3")
                + new string('v', 44)
                + videoIndex.ToString("D3");
        }

        private static string PadToLength(string value, int length, char padding)
        {
            return value + new string(padding, length - value.Length);
        }
    }
}
