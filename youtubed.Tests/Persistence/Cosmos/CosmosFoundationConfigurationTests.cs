using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosFoundationConfigurationTests
    {
        [Fact]
        public void FoundationRegistersOneSharedClientAndOnlyThreeContainerHandles()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Cosmos:ConnectionString"] =
                        "AccountEndpoint=https://localhost:8081/;" +
                        "AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;",
                    ["Cosmos:DatabaseName"] = "configuration-test"
                })
                .Build();
            var services = new ServiceCollection();

            services.AddCosmosFoundation(configuration);

            var clientRegistration = Assert.Single(
                services,
                descriptor => descriptor.ServiceType == typeof(CosmosClient));
            Assert.Equal(ServiceLifetime.Singleton, clientRegistration.Lifetime);
            using var provider = services.BuildServiceProvider();
            Assert.Same(
                provider.GetRequiredService<CosmosClient>(),
                provider.GetRequiredService<CosmosClient>());
            var context = provider.GetRequiredService<CosmosPersistenceContext>();
            Assert.Equal("configuration-test", context.Database.Id);
            Assert.Equal(
                new[]
                {
                    CosmosContainerNames.Lists,
                    CosmosContainerNames.Channels,
                    CosmosContainerNames.ShareLinks
                },
                new[] { context.Lists.Id, context.Channels.Id, context.ShareLinks.Id });
            Assert.DoesNotContain(
                typeof(CosmosPersistenceContext).GetProperties(),
                property => property.PropertyType == typeof(Container)
                    && !new[] { "Lists", "Channels", "ShareLinks" }.Contains(property.Name));
        }

        [Fact]
        public void ClientCreationRequiresConfiguredConnectionStringWithoutEchoingASecret()
        {
            const string secret = "do-not-echo-this-secret";

            var exception = Assert.Throws<System.InvalidOperationException>(() =>
                CosmosClientFactory.Create(new CosmosOptions
                {
                    ConnectionString = " ",
                    DatabaseName = secret
                }));

            Assert.DoesNotContain(secret, exception.Message);
            Assert.DoesNotContain("AccountKey", exception.Message);
        }

        [Fact]
        public void FoundationRejectsMissingDatabaseNameWithoutEchoingCredentials()
        {
            const string secret = "AccountEndpoint=https://localhost:8081/;AccountKey=secret;";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Cosmos:ConnectionString"] = secret,
                    ["Cosmos:DatabaseName"] = " "
                })
                .Build();

            var exception = Assert.Throws<System.InvalidOperationException>(() =>
                new ServiceCollection().AddCosmosFoundation(configuration));

            Assert.Contains("Cosmos:DatabaseName", exception.Message);
            Assert.DoesNotContain(secret, exception.Message);
        }

        [Fact]
        public void FoundationRequiresDatabaseNameKeyToBeExplicitlyConfigured()
        {
            const string secret = "AccountEndpoint=https://localhost:8081/;AccountKey=secret;";
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Cosmos:ConnectionString"] = secret
                })
                .Build();

            var exception = Assert.Throws<System.InvalidOperationException>(() =>
                new ServiceCollection().AddCosmosFoundation(configuration));

            Assert.Contains("Cosmos:DatabaseName", exception.Message);
            Assert.DoesNotContain(secret, exception.Message);
        }
    }
}
