using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosReleaseBudgetsTests
    {
        [Theory]
        [InlineData("small")]
        [InlineData("normal")]
        [InlineData("supported_maximum")]
        public void RepresentativeDatasetsInstantiateAndSerializeWithinTheirBudgets(string name)
        {
            var budget = CosmosReleaseBudgets.Datasets[name];
            var videosPerProjectedChannel = Math.Min(
                budget.CanonicalVideosPerChannel,
                (int)Math.Ceiling(budget.MaximumProjectedVideos / (decimal)budget.ChannelCount));
            var document = new CosmosListDocument
            {
                Id = Guid.NewGuid().ToString("D"),
                Token = new byte[40],
                Title = $"{name} representative list",
                PlaybackRate = 1,
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(45),
                Ttl = 3600,
                Channels = Enumerable.Range(0, budget.ChannelCount)
                    .Select(channel => new CosmosProjectedChannelDocument
                    {
                        Id = $"channel-{channel:D3}",
                        Url = $"https://www.youtube.com/channel/{channel:D3}",
                        Title = $"Channel {channel:D3}",
                        Thumbnail = "https://example.test/channel.png",
                        Videos = Enumerable.Range(0, videosPerProjectedChannel)
                            .Select(video => new CosmosVideoDocument
                            {
                                Id = $"video-{channel:D3}-{video:D3}",
                                Title = $"Video {video:D3}",
                                DurationTicks = TimeSpan.FromMinutes(5).Ticks,
                                PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-video),
                                Thumbnail = "https://example.test/video.png"
                            })
                            .ToArray()
                    })
                    .ToArray()
            };

            Assert.Equal(budget.ChannelCount, document.Channels.Count);
            Assert.Equal(
                Math.Min(
                    budget.MaximumProjectedVideos,
                    budget.ChannelCount * budget.CanonicalVideosPerChannel),
                document.Channels.Sum(channel => channel.Videos.Count));
            Assert.InRange(
                CosmosListProjectionPolicy.GetSerializedSizeBytes(document),
                1,
                budget.MaximumSerializedBytes - 1);

            var canonical = new CosmosChannelDocument
            {
                Id = $"canonical-{name}",
                Url = $"https://www.youtube.com/channel/canonical-{name}",
                Title = $"Canonical {name}",
                Thumbnail = "https://example.test/channel.png",
                PlaylistId = $"playlist-{name}",
                StaleAfter = DateTimeOffset.UtcNow,
                Status = "Active",
                StatusReason = "None",
                Videos = Enumerable.Range(0, budget.CanonicalVideosPerChannel)
                    .Select(video => new CosmosVideoDocument
                    {
                        Id = $"canonical-{name}-{video:D3}",
                        Title = $"Canonical video {video:D3}",
                        DurationTicks = TimeSpan.FromMinutes(5).Ticks,
                        PublishedAt = DateTimeOffset.UtcNow.AddMinutes(-video),
                        Thumbnail = "https://example.test/video.png"
                    })
                    .ToArray(),
                SubscribedListIds = Array.Empty<string>(),
                Ttl = -1
            };
            using var canonicalStream = CosmosSystemTextJsonSerializer.Instance.ToStream(canonical);
            Assert.Equal(budget.CanonicalVideosPerChannel, canonical.Videos.Count);
            Assert.InRange(canonicalStream.Length, 1, CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes - 1);
        }

        [Theory]
        [InlineData("list_page")]
        [InlineData("list_page_renewal")]
        [InlineData("membership_write")]
        [InlineData("channel_refresh")]
        [InlineData("projection_fan_out_per_list")]
        [InlineData("share_operation")]
        [InlineData("reconciliation_pass")]
        [InlineData("scheduler_operation")]
        public void EveryReleaseOperationHasPositiveRequestAndRuBudgets(string operation)
        {
            var budget = CosmosReleaseBudgets.Operations[operation];

            Assert.True(budget.MaximumRequests > 0);
            Assert.True(budget.MaximumEmulatorRu > 0);
        }

        [Fact]
        public void BudgetGuardAcceptsToleranceBoundaryAndRejectsRequestRegression()
        {
            var budget = new CosmosOperationBudget("test", 10, 100);

            CosmosReleaseBudgets.AssertWithin(budget, 12, 120);
            var exception = Assert.Throws<InvalidOperationException>(() =>
                CosmosReleaseBudgets.AssertWithin(budget, 13, 120));

            Assert.Contains("13 requests", exception.Message, StringComparison.Ordinal);
            Assert.Contains("20%", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BudgetGuardRejectsRuRegression()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                CosmosReleaseBudgets.AssertWithin(
                    new CosmosOperationBudget("test", 10, 100),
                    10,
                    120.01));

            Assert.Contains("120.01 emulator RU", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests, "throttled", true)]
        [InlineData(HttpStatusCode.RequestTimeout, "timeout", true)]
        [InlineData(HttpStatusCode.GatewayTimeout, "timeout", true)]
        [InlineData(HttpStatusCode.ServiceUnavailable, "service_unavailable", true)]
        [InlineData(HttpStatusCode.BadRequest, "failure", false)]
        public void CosmosFailuresAreClassifiedForVisibleRecovery(
            HttpStatusCode statusCode,
            string expected,
            bool transient)
        {
            var exception = new CosmosException("injected", statusCode, 1002, null, 7.5);

            Assert.Equal(expected, CosmosTransientFailurePolicy.Classify(exception));
            Assert.Equal(transient, CosmosTransientFailurePolicy.IsTransient(exception));
        }

        [Fact]
        public void CancellationAndSdkTimeoutAreDistinguished()
        {
            var exception = new OperationCanceledException("injected");

            Assert.Equal("canceled", CosmosTransientFailurePolicy.Classify(exception, true));
            Assert.Equal("timeout", CosmosTransientFailurePolicy.Classify(exception));
        }

        [Fact]
        public void RetryAndTimeoutPolicyIsBounded()
        {
            Assert.Equal(9, CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests);
            Assert.Equal(
                TimeSpan.FromSeconds(30),
                CosmosReleaseBudgets.MaxRetryWaitTimeOnRateLimitedRequests);
            Assert.Equal(TimeSpan.FromSeconds(10), CosmosReleaseBudgets.RequestTimeout);
        }

        [Fact]
        public void RequestMetricsExposeChargeLatencyStatusSubstatusRetryAndOperation()
        {
            using var listener = new RequestMetricListener();

            CosmosRequestTelemetry.Record(
                "membership_add",
                "PATCH",
                "docs",
                "throttled",
                429,
                3200,
                3,
                11.5,
                42);

            Assert.Contains(listener.Measurements, measurement =>
                measurement.Name == "cosmos.request.charge"
                && measurement.Value == 11.5
                && measurement.HasRequiredTags);
            Assert.Contains(listener.Measurements, measurement =>
                measurement.Name == "cosmos.request.latency"
                && measurement.Value == 42
                && measurement.HasRequiredTags);
            Assert.Contains(listener.Measurements, measurement =>
                measurement.Name == "cosmos.request.retry_count"
                && measurement.Value == 3
                && measurement.HasRequiredTags);
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
                Measurements.Add(new MetricMeasurement(
                    name,
                    value,
                    values.GetValueOrDefault("logical.operation") == "membership_add"
                    && values.GetValueOrDefault("sdk.operation") == "PATCH"
                    && values.GetValueOrDefault("resource.type") == "docs"
                    && values.GetValueOrDefault("outcome") == "throttled"
                    && values.GetValueOrDefault("status.code") == "429"
                    && values.GetValueOrDefault("substatus.code") == "3200"));
            }
        }

        private sealed record MetricMeasurement(
            string Name,
            double Value,
            bool HasRequiredTags);
    }
}
