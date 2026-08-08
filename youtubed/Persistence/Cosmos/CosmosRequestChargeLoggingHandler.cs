using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosRequestChargeLoggingHandler : RequestHandler
    {
        private readonly ILogger<CosmosRequestChargeLoggingHandler> _logger;

        public CosmosRequestChargeLoggingHandler(ILogger<CosmosRequestChargeLoggingHandler> logger)
        {
            _logger = logger;
        }

        public override async Task<ResponseMessage> SendAsync(
            RequestMessage request,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var sdkOperation = request.Method.Method;
            var logicalOperation = CosmosLogicalOperationScope.Current;
            var resourceType = GetResourceType(request.RequestUri);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                stopwatch.Stop();
                var substatusCode = GetSubstatusCode(response.Headers);
                var retryCount = GetRetryCount(
                    response.Headers,
                    response.Diagnostics,
                    (int)response.StatusCode);
                var outcome = response.IsSuccessStatusCode
                    ? "success"
                    : CosmosTransientFailurePolicy.Classify(new CosmosException(
                        "Cosmos request failed.",
                        response.StatusCode,
                        substatusCode,
                        response.Headers.ActivityId,
                        response.Headers.RequestCharge));
                Record(
                    logicalOperation,
                    sdkOperation,
                    resourceType,
                    outcome,
                    (int)response.StatusCode,
                    substatusCode,
                    retryCount,
                    response.Headers.RequestCharge,
                    stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogDebug(
                    "Cosmos {LogicalOperation} {SdkOperation} {ResourceType} completed in {LatencyMs:F2} ms with " +
                    "status {StatusCode}/{SubstatusCode}, {RetryCount} retries, and " +
                    "{RequestCharge:F2} RU.",
                    logicalOperation,
                    sdkOperation,
                    resourceType,
                    stopwatch.Elapsed.TotalMilliseconds,
                    (int)response.StatusCode,
                    substatusCode,
                    retryCount,
                    response.Headers.RequestCharge);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                Record(logicalOperation, sdkOperation, resourceType, "canceled", 0, 0, 0, 0, stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogInformation(
                    "Cosmos {LogicalOperation} {SdkOperation} {ResourceType} was canceled after {LatencyMs:F2} ms.",
                    logicalOperation,
                    sdkOperation,
                    resourceType,
                    stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                var cosmosException = exception as CosmosException;
                var statusCode = cosmosException == null ? 0 : (int)cosmosException.StatusCode;
                var substatusCode = cosmosException?.SubStatusCode ?? 0;
                var requestCharge = cosmosException?.RequestCharge ?? 0;
                var retryCount = cosmosException == null
                    ? 0
                    : GetRetryCount(
                        cosmosException.Headers,
                        cosmosException.Diagnostics,
                        statusCode);
                Record(
                    logicalOperation,
                    sdkOperation,
                    resourceType,
                    CosmosTransientFailurePolicy.Classify(exception),
                    statusCode,
                    substatusCode,
                    retryCount,
                    requestCharge,
                    stopwatch.Elapsed.TotalMilliseconds);
                _logger.LogError(
                    "Cosmos {LogicalOperation} {SdkOperation} {ResourceType} failed after {LatencyMs:F2} ms with " +
                    "status {StatusCode}/{SubstatusCode}, {RetryCount} retries, and " +
                    "{RequestCharge:F2} RU. ErrorClass={ErrorClass}.",
                    logicalOperation,
                    sdkOperation,
                    resourceType,
                    stopwatch.Elapsed.TotalMilliseconds,
                    statusCode,
                    substatusCode,
                    retryCount,
                    requestCharge,
                    exception.GetType().Name);
                throw;
            }
        }

        private static void Record(
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
            CosmosRequestChargeScope.Record(requestCharge);
            CosmosRequestTelemetry.Record(
                logicalOperation,
                sdkOperation,
                resourceType,
                outcome,
                statusCode,
                substatusCode,
                retryCount,
                requestCharge,
                latencyMilliseconds);
        }

        private static int GetRetryCount(
            Headers headers,
            CosmosDiagnostics diagnostics,
            int statusCode)
        {
            var value = headers?["x-ms-throttle-retry-count"];
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                return Math.Max(0, count);
            }

            if (diagnostics == null)
            {
                return 0;
            }

            try
            {
                return GetRetryCountFromDiagnostics(diagnostics.ToString(), statusCode);
            }
            catch (JsonException)
            {
                // SDK diagnostics are best-effort metadata; malformed diagnostics
                // must not change request behavior or expose their raw contents.
            }

            return 0;
        }

        internal static int GetRetryCountFromDiagnostics(
            string diagnosticsJson,
            int terminalStatusCode)
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            if (!document.RootElement.TryGetProperty("Summary", out var summary))
            {
                return 0;
            }

            var throttledAttemptCount = 0;
            foreach (var callsName in new[] { "GatewayCalls", "DirectCalls" })
            {
                if (!summary.TryGetProperty(callsName, out var calls)
                    || calls.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var statusCount in calls.EnumerateObject())
                {
                    if (statusCount.Name.StartsWith(
                            $"({(int)HttpStatusCode.TooManyRequests}, ",
                            StringComparison.Ordinal)
                        && statusCount.Value.TryGetInt32(out var attempts))
                    {
                        throttledAttemptCount += Math.Max(0, attempts);
                    }
                }
            }

            return Math.Max(
                0,
                throttledAttemptCount
                    - (terminalStatusCode == (int)HttpStatusCode.TooManyRequests ? 1 : 0));
        }

        private static int GetSubstatusCode(Headers headers)
        {
            var value = headers?["x-ms-substatus"];
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                ? Math.Max(0, code)
                : 0;
        }

        private static string GetResourceType(Uri requestUri)
        {
            var knownResourceTypes = new[]
            {
                "dbs", "colls", "docs", "sprocs", "triggers", "udfs", "users", "permissions"
            };
            var segments = requestUri?.OriginalString
                .Split(new[] { '/', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim('/'))
                .Where(segment => knownResourceTypes.Contains(segment, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            return segments?.LastOrDefault() ?? "unknown";
        }
    }
}
