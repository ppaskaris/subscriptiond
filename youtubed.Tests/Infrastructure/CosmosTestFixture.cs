using System;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Infrastructure
{
    public sealed class CosmosTestFixture : IAsyncLifetime
    {
        public const string CollectionName = "Cosmos";

        private CosmosClient _client;
        private CosmosPersistenceContext _context;

        public CosmosTestFixture()
        {
            DatabaseName = $"youtubed-tests-{Guid.NewGuid():N}";
        }

        public string DatabaseName { get; }

        public CosmosClient Client => _client
            ?? throw new InvalidOperationException("The Cosmos fixture has not been initialized.");

        public CosmosPersistenceContext Context => _context
            ?? throw new InvalidOperationException("The Cosmos fixture has not been initialized.");

        public async Task InitializeAsync()
        {
            if (!CosmosFactAttribute.IsEnabled())
            {
                return;
            }

            var emulator = CosmosEmulatorOptions.FromEnvironment();
            var options = new CosmosOptions
            {
                ConnectionString = emulator.ConnectionString,
                DatabaseName = DatabaseName
            };
            _client = CosmosClientFactory.Create(options);
            _context = await new CosmosContainerInitializer()
                .InitializeDevelopmentAsync(_client, options);
        }

        public async Task DisposeAsync()
        {
            if (_context != null)
            {
                try
                {
                    await _context.Database.DeleteAsync();
                }
                catch
                {
                    // Best-effort cleanup preserves the original emulator failure.
                }
            }

            _client?.Dispose();
        }
    }
}
