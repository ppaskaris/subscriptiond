using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

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
                        CosmosEmulatorOptions.DefaultConnectionString,
                    ["Cosmos:DatabaseName"] = "configuration-test"
                })
                .Build();
            var services = new ServiceCollection();

            services.AddCosmosFoundation(configuration);

            Assert.DoesNotContain(
                services,
                descriptor => descriptor.ServiceType == typeof(CosmosOptions));

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

            var services = new ServiceCollection();
            services.AddCosmosFoundation(configuration);
            using var provider = services.BuildServiceProvider();
            var exception = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<CosmosClient>());

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

            var services = new ServiceCollection();
            services.AddCosmosFoundation(configuration);
            using var provider = services.BuildServiceProvider();
            var exception = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<CosmosClient>());

            Assert.Contains("Cosmos:DatabaseName", exception.Message);
            Assert.DoesNotContain(secret, exception.Message);
        }

        [Fact]
        public void FoundationSupportsStandardPostConfigureTestOverrides()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Cosmos:ConnectionString"] =
                        CosmosEmulatorOptions.DefaultConnectionString,
                    ["Cosmos:DatabaseName"] = "configured-name"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddCosmosFoundation(configuration);
            services.PostConfigure<CosmosOptions>(options =>
                options.DatabaseName = "overridden-name");

            using var provider = services.BuildServiceProvider();

            Assert.Equal(
                "overridden-name",
                provider.GetRequiredService<IOptions<CosmosOptions>>().Value.DatabaseName);
            Assert.Equal(
                "overridden-name",
                provider.GetRequiredService<CosmosPersistenceContext>().Database.Id);
        }
    }
}
