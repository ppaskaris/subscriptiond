using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosOperationExecutor
    {
        private static readonly EventId RequestEvent = new(4100, "CosmosRequest");

        private readonly ILogger _logger;

        public CosmosOperationExecutor(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<ItemResponse<T>> ExecuteItemAsync<T>(
            string operation,
            string container,
            int retryCount,
            Func<CancellationToken, Task<ItemResponse<T>>> action,
            CancellationToken cancellationToken,
            bool returnNullOnNotFound = false)
        {
            return ExecuteAsync(
                operation,
                container,
                retryCount,
                action,
                cancellationToken,
                response => response.StatusCode,
                response => response.RequestCharge,
                returnNullOnNotFound);
        }

        public Task<FeedResponse<T>> ExecuteFeedPageAsync<T>(
            string operation,
            string container,
            int retryCount,
            Func<CancellationToken, Task<FeedResponse<T>>> action,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                operation,
                container,
                retryCount,
                action,
                cancellationToken,
                response => response.StatusCode,
                response => response.RequestCharge,
                returnNullOnNotFound: false);
        }

        private async Task<TResponse> ExecuteAsync<TResponse>(
            string operation,
            string container,
            int retryCount,
            Func<CancellationToken, Task<TResponse>> action,
            CancellationToken cancellationToken,
            Func<TResponse, HttpStatusCode> getStatus,
            Func<TResponse, double> getRequestCharge,
            bool returnNullOnNotFound)
            where TResponse : class
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await action(cancellationToken);
                LogRequest(
                    operation,
                    container,
                    getStatus(response),
                    getRequestCharge(response),
                    stopwatch.Elapsed,
                    retryCount);
                return response;
            }
            catch (CosmosException exception)
            {
                LogRequest(
                    operation,
                    container,
                    exception.StatusCode,
                    exception.RequestCharge,
                    stopwatch.Elapsed,
                    retryCount);
                if (returnNullOnNotFound
                    && exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                throw;
            }
        }

        private void LogRequest(
            string operation,
            string container,
            HttpStatusCode status,
            double requestCharge,
            TimeSpan elapsed,
            int retryCount)
        {
            _logger.LogInformation(
                RequestEvent,
                "Cosmos request Operation={Operation} Container={Container} RequestCount={RequestCount} RequestCharge={RequestCharge} ElapsedMilliseconds={ElapsedMilliseconds} Status={Status} RetryCount={RetryCount}",
                operation,
                container,
                1,
                requestCharge,
                Math.Min(elapsed.TotalMilliseconds, int.MaxValue),
                (int)status,
                retryCount);
        }
    }
}
