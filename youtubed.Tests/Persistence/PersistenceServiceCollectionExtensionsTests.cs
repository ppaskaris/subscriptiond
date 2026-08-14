using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Data;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence
{
    public sealed class PersistenceServiceCollectionExtensionsTests
    {
        [Fact]
        public void AddPersistence_DefaultsToSqlServer()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration();

            services.AddPersistence(configuration);

            AssertSqlServerRegistrations(services);
            using var provider = services.BuildServiceProvider();
            Assert.Equal(
                PersistenceProvider.SqlServer,
                provider.GetRequiredService<IOptions<PersistenceOptions>>().Value.Provider);
        }

        [Fact]
        public void AddPersistence_UsesConfiguredSqlServerProvider()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration(PersistenceProvider.SqlServer.ToString());

            services.AddPersistence(configuration);

            AssertSqlServerRegistrations(services);
        }

        [Fact]
        public void AddPersistence_RegistersCosmosProvider()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration(
                PersistenceProvider.Cosmos.ToString(),
                includeCosmos: true);

            services.AddPersistence(configuration);

            AssertRegistration<IListRepository, CosmosListRepository>(services);
            AssertRegistration<IShareLinkRepository, CosmosShareLinkRepository>(services);
            AssertRegistration<IChannelRepository, CosmosChannelRepository>(services);
            AssertRegistration<IExpirationPurger, CosmosExpirationPurger>(services);
        }

        [Fact]
        public void AddPersistence_CosmosRequiresCredentialsWithoutEchoingConfiguration()
        {
            const string secretName = "do-not-echo-database-name";
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                    ["Cosmos:DatabaseName"] = secretName
                })
                .Build();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddPersistence(configuration));

            Assert.Contains("Cosmos:ConnectionString", exception.Message);
            Assert.DoesNotContain(secretName, exception.Message);
        }

        private static IConfiguration CreateConfiguration(
            string provider = null,
            bool includeCosmos = false)
        {
            var values = new Dictionary<string, string>
            {
                ["ConnectionStrings:Main"] = "Server=(localdb)\\MSSQLLocalDB;Database=youtubed_tests"
            };

            if (provider != null)
            {
                values["Persistence:Provider"] = provider;
            }

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

        private static void AssertSqlServerRegistrations(IServiceCollection services)
        {
            AssertRegistration<IConnectionFactory, ConnectionStringConnectionFactory>(services);
            AssertRegistration<IListRepository, ListRepository>(services);
            AssertRegistration<IShareLinkRepository, ShareLinkRepository>(services);
            AssertRegistration<IChannelRepository, ChannelRepository>(services);
            AssertRegistration<IExpirationPurger, SqlExpirationPurger>(services);
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
