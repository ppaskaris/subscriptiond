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
    public sealed class CosmosWorkerStateStoreTests
    {
        [Fact]
        public async Task CompleteChannelRefreshPassAsync_DoesNotOverwriteForceAfterEtagConflict()
        {
            var observedRefreshAt = new DateTimeOffset(
                2026,
                7,
                18,
                12,
                0,
                0,
                TimeSpan.Zero);
            var system = new Mock<Container>();
            system
                .SetupSequence(container => container.ReadItemAsync<CosmosWorkerStateDocument>(
                    CosmosWorkerStateDocument.SchedulerId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(new CosmosWorkerStateDocument
                {
                    NextChannelRefreshAt = observedRefreshAt,
                    ChannelRefreshForceCount = 0,
                    NextPurgeAt = observedRefreshAt,
                    ETag = "etag-before-force"
                }).Object)
                .ReturnsAsync(CreateResponse(new CosmosWorkerStateDocument
                {
                    NextChannelRefreshAt = DateTimeOffset.MinValue,
                    ChannelRefreshForceCount = 1,
                    NextPurgeAt = observedRefreshAt,
                    ETag = "etag-after-force"
                }).Object);
            system
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosWorkerStateDocument>(),
                    CosmosWorkerStateDocument.SchedulerId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "concurrent force",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0));
            var store = new CosmosWorkerStateStore(
                system.Object,
                new FakeAppClock { UtcNow = observedRefreshAt });

            await store.CompleteChannelRefreshPassAsync(
                observedRefreshAt,
                0,
                observedRefreshAt.AddMinutes(30),
                CancellationToken.None);

            system.Verify(container => container.ReadItemAsync<CosmosWorkerStateDocument>(
                CosmosWorkerStateDocument.SchedulerId,
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(2));
            system.Verify(container => container.ReplaceItemAsync(
                It.IsAny<CosmosWorkerStateDocument>(),
                CosmosWorkerStateDocument.SchedulerId,
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options =>
                    options.IfMatchEtag == "etag-before-force"),
                It.IsAny<CancellationToken>()), Times.Once);
            system.VerifyNoOtherCalls();
        }

        private static Mock<ItemResponse<T>> CreateResponse<T>(T resource)
        {
            var response = new Mock<ItemResponse<T>>();
            response.SetupGet(value => value.Resource).Returns(resource);
            return response;
        }
    }
}
