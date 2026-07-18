using Microsoft.Azure.Cosmos;
using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Infrastructure
{
    public sealed class CosmosTestFixture : IAsyncLifetime
    {
        public const string CollectionName = "Cosmos";
        public const string ListsContainerName = CosmosContainerNames.Lists;
        public const string ChannelsContainerName = CosmosContainerNames.Channels;
        public const string ShareLinksContainerName = CosmosContainerNames.ShareLinks;
        public const string SystemContainerName = CosmosContainerNames.System;

        private CosmosClient _client;
        private Database _database;

        public CosmosTestFixture()
        {
            DatabaseName = $"youtubed-tests-{Guid.NewGuid():N}";
        }

        public string DatabaseName { get; }

        public Database Database => _database
            ?? throw new InvalidOperationException("The Cosmos test fixture has not been initialized.");

        public Container GetContainer(string containerName)
        {
            return Database.GetContainer(containerName);
        }

        public async Task InitializeAsync()
        {
            if (!CosmosFactAttribute.IsEnabled())
            {
                return;
            }

            var options = CosmosEmulatorOptions.FromEnvironment();
            _client = new CosmosClient(
                options.ConnectionString,
                new CosmosClientOptions
                {
                    ConnectionMode = ConnectionMode.Gateway,
                    SerializerOptions = new CosmosSerializationOptions
                    {
                        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                    }
                });

            _database = (await _client.CreateDatabaseAsync(DatabaseName)).Database;

            await new CosmosContainerInitializer().InitializeAsync(
                _database,
                new CosmosOptions());
        }

        public async Task DisposeAsync()
        {
            if (_database != null)
            {
                try
                {
                    await _database.DeleteAsync();
                }
                catch
                {
                    // Best-effort cleanup keeps failed emulator runs debuggable.
                }
            }

            _client?.Dispose();
        }
    }
}
