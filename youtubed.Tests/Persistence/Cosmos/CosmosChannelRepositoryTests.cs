using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosChannelRepositoryTests
    {
        [Theory]
        [InlineData(ChannelStatus.Active, -1, 1, true)]
        [InlineData(ChannelStatus.Active, 1, 1, false)]
        [InlineData(ChannelStatus.Unavailable, -1, 1, false)]
        [InlineData(ChannelStatus.Active, -1, 0, false)]
        public async Task GetBatchAsync_RechecksRefreshEligibility(
            ChannelStatus status,
            int staleHoursFromNow,
            int subscriptionCount,
            bool expected)
        {
            const string channelId = "channel-1";
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var document = CreateChannelDocument(channelId, "etag-1");
            document.Status = status.ToString();
            document.StaleAfter = now.AddHours(staleHoursFromNow);
            document.SubscriptionCount = subscriptionCount;
            document.SubscribedListIds = subscriptionCount == 0
                ? Array.Empty<string>()
                : new[] { listId.ToString("D") };
            var channels = new Mock<Container>();
            var lists = new Mock<Container>();
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
            if (subscriptionCount > 0)
            {
                lists
                    .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                        listId.ToString("D"),
                        It.IsAny<PartitionKey>(),
                        It.IsAny<ItemRequestOptions>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(CreateResponse(new CosmosListDocument
                    {
                        Id = listId.ToString("D"),
                        Channels = new[] { new CosmosProjectedChannelDocument { Id = channelId } }
                    }).Object);
            }
            var repository = new CosmosChannelRepository(
                channels.Object,
                lists.Object,
                new FakeAppClock { UtcNow = now });

            var result = await repository.GetBatchAsync(
                new[] { channelId },
                CancellationToken.None);

            Assert.Equal(expected, result.Count == 1);
        }

        [Fact]
        public async Task SaveRefreshResultsAsync_RereadsAndRetriesOnceAfterEtagConflict()
        {
            const string channelId = "channel-1";
            var channels = new Mock<Container>();
            var firstRead = CreateResponse(CreateChannelDocument(channelId, "etag-1"));
            var retryRead = CreateResponse(CreateChannelDocument(channelId, "etag-2"));
            channels
                .SetupSequence(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstRead.Object)
                .ReturnsAsync(retryRead.Object);
            channels
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosChannelRepository(
                channels.Object,
                new Mock<Container>().Object,
                new FakeAppClock());

            await repository.SaveRefreshResultsAsync(
                new[]
                {
                    new ChannelRefreshResult
                    {
                        Channel = new Channel
                        {
                            Id = channelId,
                            Url = "updated-url",
                            Title = "Updated",
                            Status = ChannelStatus.Active,
                            StatusReason = ChannelStatusReason.None
                        }
                    }
                },
                CancellationToken.None);

            channels.Verify(container => container.ReadItemAsync<CosmosChannelDocument>(
                channelId,
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            channels.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosChannelDocument>(document =>
                    document.ETag == "etag-2"
                    && document.Title == "Updated"
                    && document.SubscribedListIds.Count == 1),
                channelId,
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UpdateSubscriptionAsync_RemovesDeadReferenceAndSetsOrphanTtl()
        {
            const string channelId = "channel-1";
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var document = CreateChannelDocument(channelId, "etag-1");
            document.SubscribedListIds = new[] { listId.ToString("D") };
            var channels = new Mock<Container>();
            var lists = new Mock<Container>();
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException("missing", HttpStatusCode.NotFound, 0, null, 0));
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosChannelRepository(
                channels.Object,
                lists.Object,
                new FakeAppClock { UtcNow = now });

            await repository.UpdateSubscriptionAsync(channelId, listId);

            channels.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosChannelDocument>(value =>
                    value.SubscriptionCount == 0
                    && value.SubscribedListIds.Count == 0
                    && value.OrphanedAfter == now
                    && value.Ttl == (int)Constants.ChannelOrphanRetention.TotalSeconds),
                channelId,
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private static CosmosChannelDocument CreateChannelDocument(string id, string etag)
        {
            return new CosmosChannelDocument
            {
                Id = id,
                Url = "url",
                Title = "Original",
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString(),
                SubscribedListIds = new[] { Guid.Empty.ToString("D") },
                SubscriptionCount = 1,
                ETag = etag
            };
        }

        private static Mock<ItemResponse<T>> CreateResponse<T>(T resource)
        {
            var response = new Mock<ItemResponse<T>>();
            response.SetupGet(value => value.Resource).Returns(resource);
            return response;
        }
    }
}
