using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
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
            var response = await base.SendAsync(request, cancellationToken);
            _logger.LogDebug(
                "Cosmos request {Method} {RequestUri} consumed {RequestCharge:F2} RU with status {StatusCode}.",
                request.Method,
                request.RequestUri,
                response.Headers.RequestCharge,
                (int)response.StatusCode);
            return response;
        }
    }
}
