using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosInitializationHostedService : IHostedService
    {
        private readonly CosmosClient _client;
        private readonly CosmosOptions _options;
        private readonly CosmosContainerInitializer _initializer;
        private readonly IHostEnvironment _environment;

        public CosmosInitializationHostedService(
            CosmosClient client,
            IOptions<CosmosOptions> options,
            CosmosContainerInitializer initializer,
            IHostEnvironment environment)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_environment.IsDevelopment())
            {
                await _initializer.InitializeDevelopmentAsync(
                    _client,
                    _options,
                    cancellationToken);
            }
            else
            {
                await _initializer.InitializeProductionAsync(
                    _client,
                    _options,
                    cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
