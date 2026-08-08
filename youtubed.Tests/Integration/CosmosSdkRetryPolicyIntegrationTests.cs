using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosSdkRetryPolicyIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosSdkRetryPolicyIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task ConfiguredSdkPipelineRetries429ToExhaustionAndReportsActualAttempts()
        {
            var id = Guid.NewGuid().ToString("D");
            var container = _fixture.GetContainer(CosmosTestFixture.SystemContainerName);
            await container.CreateItemAsync(
                new { id, type = "retry-exhaustion-probe" },
                new PartitionKey(id));

            using var transport = new Faulting429Transport(id, int.MaxValue);
            using var httpClient = new HttpClient(transport, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var logger = new RecordingLogger();
            using var metrics = new RetryMetricListener();
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                Serializer = CosmosSystemTextJsonSerializer.Instance,
                MaxRetryAttemptsOnRateLimitedRequests =
                    CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests,
                MaxRetryWaitTimeOnRateLimitedRequests =
                    CosmosReleaseBudgets.MaxRetryWaitTimeOnRateLimitedRequests,
                RequestTimeout = CosmosReleaseBudgets.RequestTimeout,
                HttpClientFactory = () => httpClient
            };
            options.CustomHandlers.Add(new CosmosRequestChargeLoggingHandler(logger));
            using var client = new CosmosClient(
                CosmosEmulatorOptions.FromEnvironment().ConnectionString,
                options);

            ResponseMessage response;
            using (CosmosLogicalOperationScope.Begin(CosmosLogicalOperationScope.SchedulerRead))
            {
                response = await client
                    .GetDatabase(_fixture.DatabaseName)
                    .GetContainer(CosmosTestFixture.SystemContainerName)
                    .ReadItemStreamAsync(id, new PartitionKey(id));
            }
            using (response)
            {
                Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
                Assert.Equal(
                    CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests + 1,
                    transport.TargetAttemptCount);
                Assert.Null(response.Headers["x-ms-throttle-retry-count"]);
            }

            var retry = Assert.Single(metrics.Measurements, measurement =>
                measurement.Name == "cosmos.request.retry_count"
                && measurement.Tags.GetValueOrDefault("logical.operation")
                    == CosmosLogicalOperationScope.SchedulerRead
                && measurement.Tags.GetValueOrDefault("outcome") == "throttled");
            Assert.Equal(
                CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests,
                retry.Value);
            Assert.Contains(logger.Messages, message =>
                message.Contains(
                    $"{CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests} retries",
                    StringComparison.Ordinal)
                && message.Contains("status 429/3200", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, message =>
                message.Contains(id, StringComparison.Ordinal));
        }

        [CosmosFact]
        public async Task ConfiguredSdkPipelineReports429RetriesThatRecoverSuccessfully()
        {
            const int injectedFailures = 3;
            var id = Guid.NewGuid().ToString("D");
            var container = _fixture.GetContainer(CosmosTestFixture.SystemContainerName);
            await container.CreateItemAsync(
                new { id, type = "retry-success-probe" },
                new PartitionKey(id));

            using var transport = new Faulting429Transport(id, injectedFailures);
            using var httpClient = new HttpClient(transport, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            var logger = new RecordingLogger();
            using var metrics = new RetryMetricListener();
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                Serializer = CosmosSystemTextJsonSerializer.Instance,
                MaxRetryAttemptsOnRateLimitedRequests =
                    CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests,
                MaxRetryWaitTimeOnRateLimitedRequests =
                    CosmosReleaseBudgets.MaxRetryWaitTimeOnRateLimitedRequests,
                RequestTimeout = CosmosReleaseBudgets.RequestTimeout,
                HttpClientFactory = () => httpClient
            };
            options.CustomHandlers.Add(new CosmosRequestChargeLoggingHandler(logger));
            using var client = new CosmosClient(
                CosmosEmulatorOptions.FromEnvironment().ConnectionString,
                options);

            using ResponseMessage response = await ReadWithLogicalScopeAsync(
                client,
                id,
                CosmosLogicalOperationScope.ListPage);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(injectedFailures + 1, transport.TargetAttemptCount);
            Assert.Null(response.Headers["x-ms-throttle-retry-count"]);
            var retry = Assert.Single(metrics.Measurements, measurement =>
                measurement.Name == "cosmos.request.retry_count"
                && measurement.Tags.GetValueOrDefault("logical.operation")
                    == CosmosLogicalOperationScope.ListPage
                && measurement.Tags.GetValueOrDefault("outcome") == "success");
            Assert.Equal(injectedFailures, retry.Value);
            Assert.Contains(logger.Messages, message =>
                message.Contains($"{injectedFailures} retries", StringComparison.Ordinal)
                && message.Contains("status 200/0", StringComparison.Ordinal));
            Assert.DoesNotContain(logger.Messages, message =>
                message.Contains(id, StringComparison.Ordinal));
        }

        private async Task<ResponseMessage> ReadWithLogicalScopeAsync(
            CosmosClient client,
            string id,
            string logicalOperation)
        {
            using (CosmosLogicalOperationScope.Begin(logicalOperation))
            {
                return await client
                    .GetDatabase(_fixture.DatabaseName)
                    .GetContainer(CosmosTestFixture.SystemContainerName)
                    .ReadItemStreamAsync(id, new PartitionKey(id));
            }
        }

        private sealed class Faulting429Transport : DelegatingHandler
        {
            private readonly string _targetId;
            private readonly int _failureCount;
            private int _targetAttemptCount;

            internal Faulting429Transport(string targetId, int failureCount)
                : base(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                })
            {
                _targetId = targetId;
                _failureCount = failureCount;
            }

            internal int TargetAttemptCount => Volatile.Read(ref _targetAttemptCount);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.Contains(
                    $"/docs/{_targetId}",
                    StringComparison.OrdinalIgnoreCase))
                {
                    var attempt = Interlocked.Increment(ref _targetAttemptCount);
                    if (attempt <= _failureCount)
                    {
                        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                        {
                            RequestMessage = request,
                            Content = new StringContent("{\"Errors\":[\"injected throttle\"]}")
                        };
                        response.Headers.TryAddWithoutValidation("x-ms-activity-id", Guid.NewGuid().ToString("D"));
                        response.Headers.TryAddWithoutValidation("x-ms-substatus", "3200");
                        response.Headers.TryAddWithoutValidation("x-ms-request-charge", "1");
                        response.Headers.TryAddWithoutValidation("x-ms-retry-after-ms", "1");
                        return Task.FromResult(response);
                    }
                }

                return base.SendAsync(request, cancellationToken);
            }
        }

        private sealed class RecordingLogger : ILogger<CosmosRequestChargeLoggingHandler>
        {
            internal List<string> Messages { get; } = new();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                Messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }

        private sealed class RetryMetricListener : IDisposable
        {
            private readonly MeterListener _listener = new();

            internal RetryMetricListener()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CosmosRequestTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                {
                    var values = new Dictionary<string, string>();
                    foreach (var tag in tags)
                    {
                        values[tag.Key] = tag.Value?.ToString();
                    }
                    Measurements.Add(new MetricMeasurement(instrument.Name, value, values));
                });
                _listener.Start();
            }

            internal List<MetricMeasurement> Measurements { get; } = new();

            public void Dispose() => _listener.Dispose();
        }

        private sealed record MetricMeasurement(
            string Name,
            long Value,
            IReadOnlyDictionary<string, string> Tags);
    }
}
