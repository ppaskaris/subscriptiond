using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosRequestTelemetry
    {
        internal const string MeterName = "youtubed.cosmos.requests";

        private static readonly Meter Meter = new(MeterName, "1.0.0");
        private static readonly Histogram<double> RequestCharge =
            Meter.CreateHistogram<double>("cosmos.request.charge", "RU");
        private static readonly Histogram<double> Latency =
            Meter.CreateHistogram<double>("cosmos.request.latency", "ms");
        private static readonly Histogram<long> RetryCount =
            Meter.CreateHistogram<long>("cosmos.request.retry_count", "retries");

        internal static void Record(
            string logicalOperation,
            string sdkOperation,
            string resourceType,
            string outcome,
            int statusCode,
            int substatusCode,
            int retryCount,
            double requestCharge,
            double latencyMilliseconds)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object>("logical.operation", logicalOperation),
                new KeyValuePair<string, object>("sdk.operation", sdkOperation),
                new KeyValuePair<string, object>("resource.type", resourceType),
                new KeyValuePair<string, object>("outcome", outcome),
                new KeyValuePair<string, object>("status.code", statusCode),
                new KeyValuePair<string, object>("substatus.code", substatusCode)
            };
            RequestCharge.Record(requestCharge, tags);
            Latency.Record(latencyMilliseconds, tags);
            RetryCount.Record(retryCount, tags);
        }
    }
}
