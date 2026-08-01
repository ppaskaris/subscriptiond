using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosListPageTelemetry
    {
        internal const string MeterName = "youtubed.cosmos.list_page";

        private static readonly Meter Meter = new(MeterName, "1.0.0");
        private static readonly Histogram<long> RequestCount =
            Meter.CreateHistogram<long>("list_page.requests", "requests");
        private static readonly Histogram<double> RequestCharge =
            Meter.CreateHistogram<double>("list_page.request_charge", "RU");

        internal static void Record(int requestCount, double requestCharge, string outcome)
        {
            var outcomeTag = new KeyValuePair<string, object>("outcome", outcome);
            RequestCount.Record(requestCount, outcomeTag);
            RequestCharge.Record(requestCharge, outcomeTag);
        }
    }
}
