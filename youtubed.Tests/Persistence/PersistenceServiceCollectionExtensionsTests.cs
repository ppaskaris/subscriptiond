using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using youtubed.Data;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

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
        public void AddPersistence_UsesConfiguredCosmosProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAppClock, FakeAppClock>();
            var configuration = CreateConfiguration(
                PersistenceProvider.Cosmos.ToString(),
                includeCosmosConnection: true);

            services.AddPersistence(configuration);

            using var provider = services.BuildServiceProvider();
            Assert.IsType<CosmosListRepository>(provider.GetRequiredService<IListRepository>());
            Assert.IsType<CosmosShareLinkRepository>(provider.GetRequiredService<IShareLinkRepository>());
            Assert.IsType<CosmosChannelRepository>(provider.GetRequiredService<IChannelRepository>());
            Assert.IsType<CosmosListProjectionRepository>(provider.GetRequiredService<IListProjectionRepository>());
            Assert.IsType<CosmosWorkerStateStore>(provider.GetRequiredService<IWorkerStateStore>());
            Assert.IsType<CosmosExpirationPurger>(provider.GetRequiredService<IExpirationPurger>());
            Assert.IsType<CosmosConsistencyRecoveryService>(
                provider.GetRequiredService<IConsistencyRecoveryService>());
            Assert.Contains(
                provider.GetServices<IHostedService>(),
                service => service is CosmosPersistenceInitializerHostedService);
            Assert.Empty(provider.GetServices<IChannelVideoRepository>());
        }

        [Fact]
        public void AddPersistence_CosmosWithoutCredentialsFailsWithActionableMessage()
        {
            var services = new ServiceCollection();
            var configuration = CreateConfiguration(PersistenceProvider.Cosmos.ToString());

            var exception = Assert.Throws<InvalidOperationException>(
                () => services.AddPersistence(configuration));

            Assert.Contains("Cosmos:ConnectionString", exception.Message);
            Assert.Contains("Cosmos:Endpoint", exception.Message);
            Assert.Contains("Cosmos:Key", exception.Message);
        }

        private static IConfiguration CreateConfiguration(
            string provider = null,
            bool includeCosmosConnection = false)
        {
            var values = new Dictionary<string, string>
            {
                ["ConnectionStrings:Main"] = "Server=(localdb)\\MSSQLLocalDB;Database=youtubed_tests"
            };

            if (provider != null)
            {
                values["Persistence:Provider"] = provider;
            }

            if (includeCosmosConnection)
            {
                values["Cosmos:ConnectionString"] = CosmosEmulatorOptions.DefaultConnectionString;
                values["Cosmos:DatabaseName"] = "registration-tests";
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
            AssertRegistration<IChannelVideoRepository, ChannelVideoRepository>(services);
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
