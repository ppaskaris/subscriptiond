using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Data;
using youtubed.Persistence;

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
        public void AddPersistence_CosmosFailsEarlyWithTemporaryRebuildMessage()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration(PersistenceProvider.Cosmos.ToString());

            var exception = Assert.Throws<InvalidOperationException>(
                () => services.AddPersistence(configuration));

            Assert.Equal(
                "Cosmos persistence is temporarily unavailable while the simplified " +
                "provider is being rebuilt. Select 'SqlServer' with Persistence:Provider.",
                exception.Message);
            Assert.DoesNotContain(
                services,
                service => service.ServiceType == typeof(IListRepository));
        }

        private static IConfiguration CreateConfiguration(string provider = null)
        {
            var values = new Dictionary<string, string>
            {
                ["ConnectionStrings:Main"] = "Server=(localdb)\\MSSQLLocalDB;Database=youtubed_tests"
            };

            if (provider != null)
            {
                values["Persistence:Provider"] = provider;
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
            AssertRegistration<IWorkerStateStore, WorkerStateRepository>(services);
            AssertRegistration<IExpirationPurger, SqlExpirationPurger>(services);
            AssertRegistration<IListProjectionRepository, SqlListProjectionRepository>(services);
            AssertRegistration<IConsistencyRecoveryService, SqlConsistencyRecoveryService>(services);
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
