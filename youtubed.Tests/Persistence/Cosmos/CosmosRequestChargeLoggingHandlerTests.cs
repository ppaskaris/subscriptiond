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

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosRequestChargeLoggingHandlerTests
    {
        private const string Secret = "secret-share-password-never-log";

        [Fact]
        public async Task SuccessfulRequestPropagatesAllowlistedLogicalOperationWithoutSecrets()
        {
            var logger = new RecordingLogger();
            using var metrics = new RequestMetricListener();
            var request = CreateRequest();
            var response = new ResponseMessage(HttpStatusCode.OK, request, null);
            response.Headers.Set("x-ms-request-charge", "7.5");
            var handler = CreateHandler(logger, (_, _) => Task.FromResult(response));

            using (CosmosLogicalOperationScope.Begin(CosmosLogicalOperationScope.ShareConsume))
            {
                Assert.Same(response, await handler.SendAsync(request, CancellationToken.None));
            }

            var charge = Assert.Single(metrics.Measurements, value =>
                value.Name == "cosmos.request.charge"
                && value.Tags.GetValueOrDefault("logical.operation")
                    == CosmosLogicalOperationScope.ShareConsume
                && value.Tags.GetValueOrDefault("outcome") == "success");
            Assert.Equal(7.5, charge.Value);
            Assert.Equal(CosmosLogicalOperationScope.ShareConsume, charge.Tags["logical.operation"]);
            Assert.Equal("GET", charge.Tags["sdk.operation"]);
            Assert.Equal("docs", charge.Tags["resource.type"]);
            Assert.Equal("success", charge.Tags["outcome"]);
            Assert.DoesNotContain(Secret, logger.Messages.Single(), StringComparison.Ordinal);
            Assert.Contains(CosmosLogicalOperationScope.ShareConsume, logger.Messages.Single());
        }

        [Fact]
        public async Task Exhausted429ResponseRecordsRetryStatusAndVisibleThrottledOutcome()
        {
            var logger = new RecordingLogger();
            using var metrics = new RequestMetricListener();
            var request = CreateRequest();
            var response = new ResponseMessage(HttpStatusCode.TooManyRequests, request, null);
            response.Headers.Set("x-ms-request-charge", "3.25");
            response.Headers.Set("x-ms-substatus", "3200");
            response.Headers.Set("x-ms-throttle-retry-count", "9");
            var handler = CreateHandler(logger, (_, _) => Task.FromResult(response));

            using (CosmosLogicalOperationScope.Begin(CosmosLogicalOperationScope.ListDelete))
            {
                Assert.Same(response, await handler.SendAsync(request, CancellationToken.None));
            }

            var retry = Assert.Single(metrics.Measurements, value =>
                value.Name == "cosmos.request.retry_count"
                && value.Tags.GetValueOrDefault("logical.operation")
                    == CosmosLogicalOperationScope.ListDelete
                && value.Tags.GetValueOrDefault("outcome") == "throttled");
            Assert.Equal(9, retry.Value);
            Assert.Equal("throttled", retry.Tags["outcome"]);
            Assert.Equal("429", retry.Tags["status.code"]);
            Assert.Equal("3200", retry.Tags["substatus.code"]);
            Assert.Contains("status 429/3200, 9 retries", logger.Messages.Single());
            Assert.DoesNotContain(Secret, logger.Messages.Single(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task CallerCancellationIsVisibleAndPreservesCancellation()
        {
            var logger = new RecordingLogger();
            using var metrics = new RequestMetricListener();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var handler = CreateHandler(
                logger,
                (_, token) => Task.FromCanceled<ResponseMessage>(token));

            using (CosmosLogicalOperationScope.Begin(CosmosLogicalOperationScope.ProjectionFanOut))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    handler.SendAsync(CreateRequest(), cancellation.Token));
            }

            Assert.Contains(metrics.Measurements, value => value.Tags["outcome"] == "canceled");
            Assert.Contains("was canceled", logger.Messages.Single());
            Assert.DoesNotContain(Secret, logger.Messages.Single(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true, "timeout", "OperationCanceledException")]
        [InlineData(false, "service_unavailable", "CosmosException")]
        public async Task TimeoutAndServiceUnavailableFailVisiblyWithoutLoggingExceptionText(
            bool timeout,
            string expectedOutcome,
            string expectedErrorClass)
        {
            var logger = new RecordingLogger();
            using var metrics = new RequestMetricListener();
            var exception = timeout
                ? (Exception)new OperationCanceledException($"timeout {Secret}")
                : new CosmosException(
                    $"unavailable {Secret}",
                    HttpStatusCode.ServiceUnavailable,
                    20001,
                    null,
                    2.5);
            var handler = CreateHandler(
                logger,
                (_, _) => Task.FromException<ResponseMessage>(exception));

            using (CosmosLogicalOperationScope.Begin(CosmosLogicalOperationScope.Reconciliation))
            {
                var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
                    handler.SendAsync(CreateRequest(), CancellationToken.None));
                Assert.Same(exception, thrown);
            }

            Assert.Contains(metrics.Measurements, value =>
                value.Tags["outcome"] == expectedOutcome
                && value.Tags["logical.operation"] == CosmosLogicalOperationScope.Reconciliation);
            Assert.Contains($"ErrorClass={expectedErrorClass}", logger.Messages.Single());
            Assert.DoesNotContain(Secret, logger.Messages.Single(), StringComparison.Ordinal);
        }

        [Fact]
        public void ArbitraryLogicalOperationIsRejectedBeforeItCanCreateMetricCardinality()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                CosmosLogicalOperationScope.Begin($"user-{Secret}"));
        }

        [Fact]
        public void RetryDiagnosticsAggregateGatewayAndDirectBeforeTerminalAdjustment()
        {
            const string directOnlyThrottles =
                "{\"Summary\":{\"GatewayCalls\":{\"(200, 0)\":2}," +
                "\"DirectCalls\":{\"(429, 3200)\":3,\"(429, 3201)\":2,\"(200, 0)\":1}}}";
            Assert.Equal(
                5,
                CosmosRequestChargeLoggingHandler.GetRetryCountFromDiagnostics(
                    directOnlyThrottles,
                    (int)HttpStatusCode.OK));
            Assert.Equal(
                4,
                CosmosRequestChargeLoggingHandler.GetRetryCountFromDiagnostics(
                    directOnlyThrottles,
                    (int)HttpStatusCode.TooManyRequests));

            const string mixedThrottles =
                "{\"Summary\":{\"GatewayCalls\":{\"(429, 3200)\":2}," +
                "\"DirectCalls\":{\"(429, 3200)\":3}}}";
            Assert.Equal(
                5,
                CosmosRequestChargeLoggingHandler.GetRetryCountFromDiagnostics(
                    mixedThrottles,
                    (int)HttpStatusCode.OK));
            Assert.Equal(
                4,
                CosmosRequestChargeLoggingHandler.GetRetryCountFromDiagnostics(
                    mixedThrottles,
                    (int)HttpStatusCode.TooManyRequests));
        }

        private static RequestMessage CreateRequest()
        {
            return new RequestMessage(
                HttpMethod.Get,
                new Uri($"dbs/database/colls/shareLinks/docs/{Secret}", UriKind.Relative));
        }

        private static CosmosRequestChargeLoggingHandler CreateHandler(
            RecordingLogger logger,
            Func<RequestMessage, CancellationToken, Task<ResponseMessage>> send)
        {
            return new CosmosRequestChargeLoggingHandler(logger)
            {
                InnerHandler = new ScriptedHandler(send)
            };
        }

        private sealed class ScriptedHandler : RequestHandler
        {
            private readonly Func<RequestMessage, CancellationToken, Task<ResponseMessage>> _send;

            internal ScriptedHandler(
                Func<RequestMessage, CancellationToken, Task<ResponseMessage>> send)
            {
                _send = send;
            }

            public override Task<ResponseMessage> SendAsync(
                RequestMessage request,
                CancellationToken cancellationToken) => _send(request, cancellationToken);
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

        private sealed class RequestMetricListener : IDisposable
        {
            private readonly MeterListener _listener = new();

            internal RequestMetricListener()
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CosmosRequestTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                    Add(instrument.Name, value, tags));
                _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                    Add(instrument.Name, value, tags));
                _listener.Start();
            }

            internal List<MetricMeasurement> Measurements { get; } = new();

            public void Dispose() => _listener.Dispose();

            private void Add<T>(
                string name,
                double value,
                ReadOnlySpan<KeyValuePair<string, T>> tags)
            {
                var values = new Dictionary<string, string>();
                foreach (var tag in tags)
                {
                    values[tag.Key] = tag.Value is null ? null : tag.Value.ToString();
                }
                Measurements.Add(new MetricMeasurement(name, value, values));
            }
        }

        private sealed record MetricMeasurement(
            string Name,
            double Value,
            IReadOnlyDictionary<string, string> Tags);
    }
}
