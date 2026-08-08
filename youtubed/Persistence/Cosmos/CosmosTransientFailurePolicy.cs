using Microsoft.Azure.Cosmos;
using System;
using System.Net;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosTransientFailurePolicy
    {
        internal static string Classify(Exception exception, bool callerCanceled = false)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (exception is OperationCanceledException)
            {
                return callerCanceled ? "canceled" : "timeout";
            }

            if (exception is CosmosException cosmosException)
            {
                return cosmosException.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => "throttled",
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => "timeout",
                    HttpStatusCode.ServiceUnavailable => "service_unavailable",
                    _ => "failure"
                };
            }

            return "failure";
        }

        internal static bool IsTransient(Exception exception)
        {
            var classification = Classify(exception);
            return classification is "throttled" or "timeout" or "service_unavailable";
        }
    }
}
