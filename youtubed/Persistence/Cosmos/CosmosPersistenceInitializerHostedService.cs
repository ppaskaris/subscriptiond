using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosPersistenceInitializerHostedService : IHostedService
    {
        private readonly CosmosClient _client;
        private readonly CosmosOptions _options;
        private readonly CosmosContainerInitializer _containerInitializer;
        private readonly ILogger<CosmosPersistenceInitializerHostedService> _logger;

        public CosmosPersistenceInitializerHostedService(
            CosmosClient client,
            CosmosOptions options,
            CosmosContainerInitializer containerInitializer,
            ILogger<CosmosPersistenceInitializerHostedService> logger)
        {
            _client = client;
            _options = options;
            _containerInitializer = containerInitializer;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var database = (await _client.CreateDatabaseIfNotExistsAsync(
                _options.DatabaseName,
                cancellationToken: cancellationToken)).Database;
            await _containerInitializer.InitializeAsync(database, _options, cancellationToken);
            _logger.LogInformation(
                "Cosmos persistence initialized database {DatabaseName} with lists, channels, share links, system, and recovery containers.",
                _options.DatabaseName);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
