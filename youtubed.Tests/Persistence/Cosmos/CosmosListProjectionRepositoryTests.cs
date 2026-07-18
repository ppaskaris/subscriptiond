using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosListProjectionRepositoryTests
    {
        [Fact]
        public async Task UpdateProjectedChannelsAsync_ReplacesOnlyBatchChannelsInEachList()
        {
            var listId = Guid.NewGuid();
            var untouched = CreateProjectedChannel("untouched", "Untouched");
            var document = CreateListDocument(
                listId,
                "etag-1",
                CreateProjectedChannel("channel-1", "Before One"),
                untouched,
                CreateProjectedChannel("channel-2", "Before Two"));
            var lists = new Mock<Container>();
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                new Mock<Container>().Object,
                new FakeAppClock());

            await repository.UpdateProjectedChannelsAsync(
                new[]
                {
                    CreateChannel("channel-1", "After One", listId),
                    CreateChannel("channel-2", "After Two", listId)
                },
                CancellationToken.None);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(value =>
                    value.Channels.Count == 3
                    && value.Channels[0].Title == "After One"
                    && ReferenceEquals(value.Channels[1], untouched)
                    && value.Channels[2].Title == "After Two"),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-1"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateProjectedChannelsAsync_RereadsAndReappliesAfterEtagConflict()
        {
            var listId = Guid.NewGuid();
            var firstRead = CreateListDocument(
                listId,
                "etag-1",
                CreateProjectedChannel("channel-1", "Before"),
                CreateProjectedChannel("untouched", "Original"));
            var retryRead = CreateListDocument(
                listId,
                "etag-2",
                CreateProjectedChannel("channel-1", "Before"),
                CreateProjectedChannel("untouched", "Concurrent Update"));
            var lists = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(firstRead).Object)
                .ReturnsAsync(CreateResponse(retryRead).Object);
            lists
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(CreateCosmosException(HttpStatusCode.PreconditionFailed))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                new Mock<Container>().Object,
                new FakeAppClock());

            await repository.UpdateProjectedChannelsAsync(
                new[] { CreateChannel("channel-1", "After", listId) },
                CancellationToken.None);

            lists.Verify(container => container.ReadItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(value =>
                    value.ETag == "etag-2"
                    && value.Channels[0].Title == "After"
                    && value.Channels[1].Title == "Concurrent Update"),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateProjectedChannelsAsync_ThrowsAfterSecondEtagConflict()
        {
            var listId = Guid.NewGuid();
            var lists = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(CreateListDocument(
                    listId,
                    "etag-1",
                    CreateProjectedChannel("channel-1", "Before"))).Object)
                .ReturnsAsync(CreateResponse(CreateListDocument(
                    listId,
                    "etag-2",
                    CreateProjectedChannel("channel-1", "Before"))).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(CreateCosmosException(HttpStatusCode.PreconditionFailed));
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                new Mock<Container>().Object,
                new FakeAppClock());

            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.UpdateProjectedChannelsAsync(
                    new[] { CreateChannel("channel-1", "After", listId) },
                    CancellationToken.None));

            lists.Verify(container => container.ReplaceItemAsync(
                It.IsAny<CosmosListDocument>(),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task UpdateProjectedChannelsAsync_RepairsMissingListReferenceAndSetsOrphanTtl()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string channelId = "channel-1";
            var channelDocument = new CosmosChannelDocument
            {
                Id = channelId,
                SubscribedListIds = new[] { listId.ToString("D"), "malformed-list-id" },
                SubscriptionCount = 2,
                Ttl = -1,
                ETag = "channel-etag"
            };
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(CreateCosmosException(HttpStatusCode.NotFound));
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(channelDocument).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.UpdateProjectedChannelsAsync(
                new[] { CreateChannel(channelId, "After", listId) },
                CancellationToken.None);

            channels.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosChannelDocument>(value =>
                    value.SubscribedListIds.Count == 0
                    && value.SubscriptionCount == 0
                    && value.OrphanedAfter == now
                    && value.Ttl == (int)Constants.ChannelOrphanRetention.TotalSeconds),
                channelId,
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "channel-etag"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateProjectedChannelsAsync_DoesNotRepairReferenceReaddedBeforeChannelWrite()
        {
            var listId = Guid.NewGuid();
            const string channelId = "channel-1";
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(CreateListDocument(listId, "list-etag-1")).Object)
                .ReturnsAsync(CreateResponse(CreateListDocument(
                    listId,
                    "list-etag-2",
                    CreateProjectedChannel(channelId, "Readded"))).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(new CosmosChannelDocument
                {
                    Id = channelId,
                    SubscribedListIds = new[] { listId.ToString("D") },
                    SubscriptionCount = 1,
                    Ttl = -1,
                    ETag = "channel-etag"
                }).Object);
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock());

            await repository.UpdateProjectedChannelsAsync(
                new[] { CreateChannel(channelId, "After", listId) },
                CancellationToken.None);

            channels.Verify(container => container.ReplaceItemAsync(
                It.IsAny<CosmosChannelDocument>(),
                It.IsAny<string>(),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task UpdateProjectedChannelsAsync_RevalidatesReferenceAfterChannelEtagConflict()
        {
            var listId = Guid.NewGuid();
            const string channelId = "channel-1";
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(CreateListDocument(listId, "list-etag-1")).Object)
                .ThrowsAsync(CreateCosmosException(HttpStatusCode.NotFound))
                .ReturnsAsync(CreateResponse(CreateListDocument(
                    listId,
                    "list-etag-2",
                    CreateProjectedChannel(channelId, "Readded"))).Object);
            channels
                .SetupSequence(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(CreateChannelDocument(
                    channelId,
                    listId,
                    "channel-etag-1")).Object)
                .ReturnsAsync(CreateResponse(CreateChannelDocument(
                    channelId,
                    listId,
                    "channel-etag-2")).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(CreateCosmosException(HttpStatusCode.PreconditionFailed));
            var repository = new CosmosListProjectionRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock());

            await repository.UpdateProjectedChannelsAsync(
                new[] { CreateChannel(channelId, "After", listId) },
                CancellationToken.None);

            lists.Verify(container => container.ReadItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
            channels.Verify(container => container.ReplaceItemAsync(
                It.IsAny<CosmosChannelDocument>(),
                channelId,
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private static Channel CreateChannel(string id, string title, params Guid[] listIds)
        {
            return new Channel
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = title,
                Thumbnail = $"{id}.png",
                StaleAfter = DateTimeOffset.UtcNow,
                Status = ChannelStatus.Active,
                StatusReason = ChannelStatusReason.None,
                SubscribedListIds = listIds,
                SubscriptionCount = listIds.Length
            };
        }

        private static CosmosListDocument CreateListDocument(
            Guid id,
            string etag,
            params CosmosProjectedChannelDocument[] channels)
        {
            return new CosmosListDocument
            {
                Id = id.ToString("D"),
                Token = new byte[] { 1 },
                Title = "List",
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                Channels = channels,
                ETag = etag
            };
        }

        private static CosmosProjectedChannelDocument CreateProjectedChannel(string id, string title)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = id,
                Title = title
            };
        }

        private static CosmosChannelDocument CreateChannelDocument(
            string channelId,
            Guid listId,
            string etag)
        {
            return new CosmosChannelDocument
            {
                Id = channelId,
                SubscribedListIds = new[] { listId.ToString("D") },
                SubscriptionCount = 1,
                Ttl = -1,
                ETag = etag
            };
        }

        private static CosmosException CreateCosmosException(HttpStatusCode statusCode)
        {
            return new CosmosException("Cosmos failure", statusCode, 0, null, 0);
        }

        private static Mock<ItemResponse<T>> CreateResponse<T>(T resource)
        {
            var response = new Mock<ItemResponse<T>>();
            response.SetupGet(value => value.Resource).Returns(resource);
            return response;
        }
    }
}
