using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosOperationExecutorTests
    {
        [Fact]
        public async Task ItemSuccessPreservesCancellationAndEmitsCompleteTelemetry()
        {
            var logger = new CosmosRequestRecorder<object>();
            var executor = new CosmosOperationExecutor(logger);
            using var cancellation = new CancellationTokenSource();
            var response = CreateItemResponse(HttpStatusCode.OK, requestCharge: 1.25);
            var observedToken = CancellationToken.None;

            var result = await executor.ExecuteItemAsync(
                "pointRead",
                CosmosContainerNames.Lists,
                retryCount: 1,
                token =>
                {
                    observedToken = token;
                    return Task.FromResult(response);
                },
                cancellation.Token);

            Assert.Same(response, result);
            Assert.Equal(cancellation.Token, observedToken);
            var request = Assert.Single(logger.Records);
            Assert.Equal("pointRead", request.Operation);
            Assert.Equal(CosmosContainerNames.Lists, request.Container);
            Assert.Equal(1, request.RequestCount);
            Assert.Equal(1.25, request.RequestCharge);
            Assert.InRange(request.ElapsedMilliseconds, 0, double.MaxValue);
            Assert.Equal((int)HttpStatusCode.OK, request.Status);
            Assert.Equal(1, request.RetryCount);
        }

        [Fact]
        public async Task CosmosFailureIsLoggedSafelyAndRethrownUnchanged()
        {
            const string listId = "private-list-id";
            const string password = "secret-share-password";
            const string token = "secret-list-token";
            const string connectionString = "AccountEndpoint=https://private/;AccountKey=private-key";
            var logger = new CosmosRequestRecorder<object>();
            var executor = new CosmosOperationExecutor(logger);
            var exception = new CosmosException(
                $"Failure {listId} {password} {token} {connectionString} diagnostics dbs/private",
                HttpStatusCode.TooManyRequests,
                subStatusCode: 0,
                activityId: "private-activity",
                requestCharge: 3.5);

            var thrown = await Assert.ThrowsAsync<CosmosException>(() =>
                executor.ExecuteItemAsync<CosmosListDocument>(
                    "replace",
                    CosmosContainerNames.Lists,
                    retryCount: 1,
                    _ => Task.FromException<ItemResponse<CosmosListDocument>>(exception),
                    CancellationToken.None));

            Assert.Same(exception, thrown);
            var request = Assert.Single(logger.Records);
            Assert.Equal((int)HttpStatusCode.TooManyRequests, request.Status);
            Assert.Equal(3.5, request.RequestCharge);
            Assert.Equal(1, request.RetryCount);
            Assert.DoesNotContain(listId, request.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(password, request.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(token, request.Rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("AccountKey", request.Rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("diagnostic", request.Rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dbs/", request.Rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-activity", request.Rendered, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PointReadNotFoundIsReturnedOnlyWhenCallerRequestsIt()
        {
            var logger = new CosmosRequestRecorder<object>();
            var executor = new CosmosOperationExecutor(logger);
            var exception = CreateException(HttpStatusCode.NotFound, requestCharge: 1);

            var missing = await executor.ExecuteItemAsync<CosmosListDocument>(
                "pointRead",
                CosmosContainerNames.Lists,
                retryCount: 0,
                _ => Task.FromException<ItemResponse<CosmosListDocument>>(exception),
                CancellationToken.None,
                returnNullOnNotFound: true);
            var thrown = await Assert.ThrowsAsync<CosmosException>(() =>
                executor.ExecuteItemAsync<CosmosListDocument>(
                    "delete",
                    CosmosContainerNames.Lists,
                    retryCount: 0,
                    _ => Task.FromException<ItemResponse<CosmosListDocument>>(exception),
                    CancellationToken.None));

            Assert.Null(missing);
            Assert.Same(exception, thrown);
            Assert.Equal(2, logger.Records.Count);
            Assert.All(
                logger.Records,
                request => Assert.Equal((int)HttpStatusCode.NotFound, request.Status));
        }

        [Fact]
        public async Task FeedPagesEmitSuccessAndFailureTelemetry()
        {
            var logger = new CosmosRequestRecorder<object>();
            var executor = new CosmosOperationExecutor(logger);
            var response = CreateFeedResponse(HttpStatusCode.OK, requestCharge: 2.75);
            var exception = CreateException(HttpStatusCode.ServiceUnavailable, requestCharge: 0.5);

            var result = await executor.ExecuteFeedPageAsync(
                "query",
                CosmosContainerNames.ShareLinks,
                retryCount: 0,
                _ => Task.FromResult(response),
                CancellationToken.None);
            var thrown = await Assert.ThrowsAsync<CosmosException>(() =>
                executor.ExecuteFeedPageAsync<CosmosShareLinkDocument>(
                    "query",
                    CosmosContainerNames.ShareLinks,
                    retryCount: 1,
                    _ => Task.FromException<FeedResponse<CosmosShareLinkDocument>>(exception),
                    CancellationToken.None));

            Assert.Same(response, result);
            Assert.Same(exception, thrown);
            Assert.Collection(
                logger.Records,
                request =>
                {
                    Assert.Equal((int)HttpStatusCode.OK, request.Status);
                    Assert.Equal(2.75, request.RequestCharge);
                    Assert.Equal(0, request.RetryCount);
                },
                request =>
                {
                    Assert.Equal((int)HttpStatusCode.ServiceUnavailable, request.Status);
                    Assert.Equal(0.5, request.RequestCharge);
                    Assert.Equal(1, request.RetryCount);
                });
        }

        [Fact]
        public async Task CancellationTokenIsPreservedWithoutInventingRequestTelemetry()
        {
            var logger = new CosmosRequestRecorder<object>();
            var executor = new CosmosOperationExecutor(logger);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var observedToken = CancellationToken.None;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                executor.ExecuteItemAsync<CosmosListDocument>(
                    "pointRead",
                    CosmosContainerNames.Lists,
                    retryCount: 0,
                    token =>
                    {
                        observedToken = token;
                        token.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("Unreachable.");
                    },
                    cancellation.Token));

            Assert.Equal(cancellation.Token, observedToken);
            Assert.Empty(logger.Records);
        }

        private static ItemResponse<CosmosListDocument> CreateItemResponse(
            HttpStatusCode status,
            double requestCharge)
        {
            var response = new Mock<ItemResponse<CosmosListDocument>>(MockBehavior.Strict);
            response.SetupGet(value => value.StatusCode).Returns(status);
            response.SetupGet(value => value.RequestCharge).Returns(requestCharge);
            return response.Object;
        }

        private static FeedResponse<CosmosShareLinkDocument> CreateFeedResponse(
            HttpStatusCode status,
            double requestCharge)
        {
            var response = new Mock<FeedResponse<CosmosShareLinkDocument>>(MockBehavior.Strict);
            response.SetupGet(value => value.StatusCode).Returns(status);
            response.SetupGet(value => value.RequestCharge).Returns(requestCharge);
            return response.Object;
        }

        private static CosmosException CreateException(
            HttpStatusCode status,
            double requestCharge)
        {
            return new CosmosException(
                "Cosmos operation failed.",
                status,
                subStatusCode: 0,
                activityId: string.Empty,
                requestCharge: requestCharge);
        }
    }
}
