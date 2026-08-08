using System;
using System.Collections.Generic;

namespace youtubed.Persistence.Cosmos
{
    internal sealed record CosmosOperationBudget(
        string Operation,
        int MaximumRequests,
        double MaximumEmulatorRu);

    internal sealed record CosmosDatasetBudget(
        string Name,
        int ChannelCount,
        int CanonicalVideosPerChannel,
        int MaximumProjectedVideos,
        int MaximumSerializedBytes,
        double PointReadEmulatorRu,
        double ReplaceEmulatorRu);

    internal static class CosmosReleaseBudgets
    {
        internal const double RegressionTolerance = 0.20;
        internal const int SmallChannelCount = 1;
        internal const int SmallVideosPerChannel = 5;
        internal const int NormalChannelCount = 20;
        internal const int NormalVideosPerChannel = 20;
        internal const int MaximumChannelCount = CosmosListProjectionPolicy.MaxChannelsPerList;
        internal const int MaximumVideosPerChannel =
            CosmosListProjectionPolicy.MaxCanonicalVideosPerChannel;
        internal const int MaximumProjectedVideos =
            CosmosListProjectionPolicy.MaxProjectedVideosPerList;

        internal const int MaxRetryAttemptsOnRateLimitedRequests = 9;
        internal static readonly TimeSpan MaxRetryWaitTimeOnRateLimitedRequests =
            TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        internal static readonly IReadOnlyDictionary<string, CosmosOperationBudget> Operations =
            new Dictionary<string, CosmosOperationBudget>(StringComparer.Ordinal)
            {
                ["list_page"] = new("list_page", 1, 10),
                ["list_page_renewal"] = new("list_page_renewal", 2, 25),
                ["membership_write"] = new("membership_write", 22, 1_200),
                ["channel_refresh"] = new("channel_refresh", 15, 1_500),
                ["projection_fan_out_per_list"] = new("projection_fan_out_per_list", 8, 3_000),
                ["share_operation"] = new("share_operation", 3, 30),
                ["reconciliation_pass"] = new("reconciliation_pass", 100, 2_000),
                ["scheduler_operation"] = new("scheduler_operation", 3, 30)
            };

        internal static readonly IReadOnlyDictionary<string, CosmosDatasetBudget> Datasets =
            new Dictionary<string, CosmosDatasetBudget>(StringComparer.Ordinal)
            {
                ["small"] = new("small", 1, 5, 5, 64_000, 10, 50),
                ["normal"] = new("normal", 20, 20, 400, 1_000_000, 200, 1_500),
                ["supported_maximum"] = new(
                    "supported_maximum",
                    MaximumChannelCount,
                    MaximumVideosPerChannel,
                    MaximumProjectedVideos,
                    CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes,
                    CosmosListProjectionPolicy.PointReadRuBudget,
                    CosmosListProjectionPolicy.ProjectionWriteRuBudget)
            };

        internal static void AssertWithin(
            CosmosOperationBudget budget,
            int requestCount,
            double emulatorRu)
        {
            ArgumentNullException.ThrowIfNull(budget);
            var toleratedRequests = checked((int)Math.Ceiling(
                budget.MaximumRequests * (1 + RegressionTolerance)));
            var toleratedRu = budget.MaximumEmulatorRu * (1 + RegressionTolerance);
            if (requestCount > toleratedRequests || emulatorRu > toleratedRu)
            {
                throw new InvalidOperationException(
                    $"Cosmos operation '{budget.Operation}' exceeded its release budget: " +
                    $"observed {requestCount} requests/{emulatorRu:F2} emulator RU; " +
                    $"allowed {toleratedRequests} requests/{toleratedRu:F2} RU including " +
                    $"the {RegressionTolerance:P0} regression tolerance.");
            }
        }
    }
}
