using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence
{
    public sealed class PersistenceServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddPersistence_RegistersCosmosRepositories()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration(includeCosmos: true);

            services.AddPersistence(configuration);

            AssertRegistration<IListRepository, CosmosListRepository>(services);
            AssertRegistration<IShareLinkRepository, CosmosShareLinkRepository>(services);
            AssertRegistration<IChannelRepository, CosmosChannelRepository>(services);
        }

        [Fact]
        public void AddPersistence_CosmosRequiresCredentialsWithoutEchoingConfiguration()
        {
            const string secretName = "do-not-echo-database-name";
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Cosmos:DatabaseName"] = secretName
                })
                .Build();

            services.AddPersistence(configuration);
            using var provider = services.BuildServiceProvider();
            var exception = Assert.Throws<OptionsValidationException>(() =>
                provider.GetRequiredService<Microsoft.Azure.Cosmos.CosmosClient>());

            Assert.Contains("Cosmos:ConnectionString", exception.Message);
            Assert.DoesNotContain(secretName, exception.Message);
        }

        private static IConfiguration CreateConfiguration(bool includeCosmos = false)
        {
            var values = new Dictionary<string, string>();

            if (includeCosmos)
            {
                values["Cosmos:ConnectionString"] =
                    "AccountEndpoint=https://localhost:8081/;AccountKey=test-key;";
                values["Cosmos:DatabaseName"] = "registration-test";
            }

            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        private static void AssertRegistration<TService, TImplementation>(IServiceCollection services)
        {
            var registration = Assert.Single(
                services,
                service => service.ServiceType == typeof(TService));
            Assert.True(
                registration.ImplementationType == typeof(TImplementation)
                    || registration.ImplementationInstance is TImplementation);
        }
    }
}
