using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosRecoveryTelemetry
    {
        internal const string MeterName = "youtubed.cosmos.recovery.persistence";

        private static readonly Meter Meter = new(MeterName, "1.0.0");
        private static readonly Counter<long> ConflictCounter =
            Meter.CreateCounter<long>("recovery.persistence.etag_conflicts");
        private static readonly Counter<long> RetryCounter =
            Meter.CreateCounter<long>("recovery.persistence.retries");
        private static readonly Histogram<long> PendingItems =
            Meter.CreateHistogram<long>("recovery.pending.items", "items");
        private static readonly Histogram<double> ConvergenceLatency =
            Meter.CreateHistogram<double>("recovery.convergence.latency", "ms");

        internal static void RecordConflict(
            string component,
            string operation,
            bool retry)
        {
            var tags = new TagList
            {
                { "component", component },
                { "operation", operation }
            };
            ConflictCounter.Add(1, tags);
            if (retry)
            {
                RetryCounter.Add(1, tags);
            }
        }

        internal static void RecordPending(string workKind, int count)
        {
            PendingItems.Record(
                count,
                new KeyValuePair<string, object>("work.kind", workKind));
        }

        internal static void RecordConvergence(
            string workKind,
            DateTimeOffset now,
            DateTimeOffset? startedAt)
        {
            ConvergenceLatency.Record(
                startedAt.HasValue
                    ? Math.Max(0, (now - startedAt.Value).TotalMilliseconds)
                    : 0,
                new KeyValuePair<string, object>("work.kind", workKind));
        }
    }
}
