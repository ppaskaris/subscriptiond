using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosShareLinkRepositoryTests
    {
        [Fact]
        public async Task ConsumeAsync_RereadsListAndRetriesOnceAfterEtagConflict()
        {
            const string password = "consume-once";
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var shareLinks = new Mock<Container>();
            var lists = new Mock<Container>();
            var firstLink = CreateShareLinkDocument(password, listId, now, "etag-1");
            var retryLink = CreateShareLinkDocument(password, listId, now, "etag-2");
            var list = new CosmosListDocument
            {
                Id = listId.ToString("D"),
                Token = new byte[] { 7 }
            };

            shareLinks
                .SetupSequence(container => container.ReadItemAsync<CosmosShareLinkDocument>(
                    password,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(firstLink).Object)
                .ReturnsAsync(CreateResponse(retryLink).Object);
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    list.Id,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(list).Object);
            shareLinks
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosShareLinkDocument>(),
                    password,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosShareLinkDocument>(null).Object);
            var repository = new CosmosShareLinkRepository(
                shareLinks.Object,
                lists.Object,
                new FakeAppClock { UtcNow = now });

            var consumed = await repository.ConsumeAsync(password, now);

            Assert.NotNull(consumed);
            Assert.Equal(listId, consumed.ListId);
            Assert.Equal(list.Token, consumed.Token);
            lists.Verify(container => container.ReadItemAsync<CosmosListDocument>(
                list.Id,
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            shareLinks.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosShareLinkDocument>(document =>
                    document.ETag == "etag-2"
                    && document.UsedAt == now),
                password,
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ConsumeAsync_ThrowsAfterSecondEtagConflict()
        {
            const string password = "repeated-conflict";
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var shareLinks = new Mock<Container>();
            var lists = new Mock<Container>();
            var list = new CosmosListDocument
            {
                Id = listId.ToString("D"),
                Token = new byte[] { 7 }
            };

            shareLinks
                .SetupSequence(container => container.ReadItemAsync<CosmosShareLinkDocument>(
                    password,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(
                    CreateShareLinkDocument(password, listId, now, "etag-1")).Object)
                .ReturnsAsync(CreateResponse(
                    CreateShareLinkDocument(password, listId, now, "etag-2")).Object);
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    list.Id,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(list).Object);
            shareLinks
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosShareLinkDocument>(),
                    password,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0));
            var repository = new CosmosShareLinkRepository(
                shareLinks.Object,
                lists.Object,
                new FakeAppClock { UtcNow = now });

            var exception = await Assert.ThrowsAsync<CosmosException>(
                () => repository.ConsumeAsync(password, now));

            Assert.Equal(HttpStatusCode.PreconditionFailed, exception.StatusCode);
            shareLinks.Verify(container => container.ReplaceItemAsync(
                It.IsAny<CosmosShareLinkDocument>(),
                password,
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        private static CosmosShareLinkDocument CreateShareLinkDocument(
            string password,
            Guid listId,
            DateTimeOffset now,
            string etag)
        {
            return new CosmosShareLinkDocument
            {
                Id = password,
                ListId = listId.ToString("D"),
                CreatedAt = now,
                ExpiresAfter = now.AddHours(1),
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
