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
    public sealed class CosmosListRepositoryTests
    {
        [Fact]
        public async Task UpdateAsync_RecomputesTtlFromAbsoluteExpirationBeforeReplacing()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var document = CreateListDocument(listId, "etag-1");
            document.ExpiredAfter = now.AddHours(1);
            document.Ttl = (int)TimeSpan.FromDays(45).TotalSeconds;
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
            var repository = new CosmosListRepository(
                lists.Object,
                new Mock<Container>().Object,
                new FakeAppClock { UtcNow = now });

            await repository.UpdateAsync(listId, "Updated", 1.5m);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(value => value.Ttl == 3600),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddChannelAsync_RereadsAndRetriesOnceAfterEtagConflict()
        {
            const string channelId = "channel-1";
            var listId = Guid.NewGuid();
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            var channelResponse = CreateResponse(new CosmosChannelDocument
            {
                Id = channelId,
                Title = "Channel",
                Url = "https://www.youtube.com/channel/channel-1",
                Thumbnail = "channel.png",
                PlaylistId = "playlist-1",
                StaleAfter = DateTimeOffset.UtcNow,
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString()
            });
            var firstRead = CreateResponse(CreateListDocument(listId, "etag-1"));
            var retryRead = CreateResponse(CreateListDocument(listId, "etag-2"));

            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channelResponse.Object);
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstRead.Object)
                .ReturnsAsync(retryRead.Object);
            lists
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);

            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock());

            await repository.AddChannelAsync(listId, channelId);

            lists.Verify(container => container.ReadItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.ETag == "etag-2"
                    && document.Channels.Count == 1
                    && document.Channels[0].Id == channelId),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private static CosmosListDocument CreateListDocument(Guid id, string etag)
        {
            return new CosmosListDocument
            {
                Id = id.ToString("D"),
                Token = new byte[] { 1 },
                Title = "List",
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1),
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
