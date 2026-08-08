using System;
using System.Linq;
using Dapper;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using youtubed.Data;
using youtubed.Persistence.Cosmos;
using youtubed.Services;

namespace youtubed.Persistence
{
    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(PersistenceOptions.SectionName);
            services.Configure<PersistenceOptions>(section);

            var options = section.Get<PersistenceOptions>() ?? new PersistenceOptions();
            return options.Provider switch
            {
                PersistenceProvider.SqlServer => services.AddSqlServerPersistence(configuration),
                PersistenceProvider.Cosmos => services.AddCosmosPersistence(configuration),
                _ => throw new InvalidOperationException(
                    $"Unsupported persistence provider '{options.Provider}'.")
            };
        }

        public static IServiceCollection AddSqlServerPersistence(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

            services.AddSingleton<IConnectionFactory>(
                new ConnectionStringConnectionFactory(configuration.GetConnectionString("Main")));
            services.AddSingleton<IListRepository, ListRepository>();
            services.AddSingleton<IShareLinkRepository, ShareLinkRepository>();
            services.AddSingleton<IChannelRepository, ChannelRepository>();
            services.AddSingleton<IWorkerStateStore, WorkerStateRepository>();
            services.AddSingleton<IExpirationPurger, SqlExpirationPurger>();
            services.AddSingleton<IListProjectionRepository, SqlListProjectionRepository>();
            services.AddSingleton<IConsistencyRecoveryService, SqlConsistencyRecoveryService>();

            return services;
        }

        public static IServiceCollection AddCosmosPersistence(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(CosmosOptions.SectionName);
            var options = section.Get<CosmosOptions>() ?? new CosmosOptions();
            ValidateCosmosOptions(options);
            var recoverySection = configuration.GetSection(CosmosRecoveryOptions.SectionName);
            var recoveryOptions =
                recoverySection.Get<CosmosRecoveryOptions>() ?? new CosmosRecoveryOptions();
            recoveryOptions.Validate();

            services.Configure<CosmosOptions>(section);
            services.Configure<CosmosRecoveryOptions>(recoverySection);
            services.AddSingleton(provider =>
            {
                var configuredOptions = provider.GetRequiredService<IOptions<CosmosOptions>>().Value;
                var clientOptions = new CosmosClientOptions
                {
                    Serializer = CosmosSystemTextJsonSerializer.Instance,
                    MaxRetryAttemptsOnRateLimitedRequests =
                        CosmosReleaseBudgets.MaxRetryAttemptsOnRateLimitedRequests,
                    MaxRetryWaitTimeOnRateLimitedRequests =
                        CosmosReleaseBudgets.MaxRetryWaitTimeOnRateLimitedRequests,
                    RequestTimeout = CosmosReleaseBudgets.RequestTimeout
                };
                clientOptions.CustomHandlers.Add(new CosmosRequestChargeLoggingHandler(
                    provider.GetRequiredService<ILogger<CosmosRequestChargeLoggingHandler>>()));

                return string.IsNullOrWhiteSpace(configuredOptions.ConnectionString)
                    ? new CosmosClient(
                        configuredOptions.Endpoint,
                        configuredOptions.Key,
                        clientOptions)
                    : new CosmosClient(configuredOptions.ConnectionString, clientOptions);
            });
            services.AddSingleton(options);
            services.AddSingleton(recoveryOptions);
            services.AddSingleton<CosmosPersistenceContext>();
            services.AddSingleton<CosmosContainerInitializer>();
            services.AddSingleton<IHostedService, CosmosPersistenceInitializerHostedService>();

            services.AddSingleton<IListRepository>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosListRepository(
                    context.Lists,
                    context.Channels,
                    context.Recovery,
                    provider.GetRequiredService<IAppClock>(),
                    provider.GetRequiredService<CosmosRecoveryOptions>(),
                    provider.GetRequiredService<IWorkerStateStore>(),
                    provider.GetService<IWorkerWakeSignal>());
            });
            services.AddSingleton<IShareLinkRepository>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosShareLinkRepository(
                    context.ShareLinks,
                    context.Lists,
                    provider.GetRequiredService<IAppClock>());
            });
            services.AddSingleton<IChannelRepository>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosChannelRepository(
                    context.Channels,
                    context.Lists,
                    context.Recovery,
                    provider.GetRequiredService<IAppClock>(),
                    provider.GetRequiredService<CosmosRecoveryOptions>());
            });
            services.AddSingleton<IListProjectionRepository>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosListProjectionRepository(
                    context.Lists,
                    context.Channels,
                    context.Recovery,
                    provider.GetRequiredService<IAppClock>(),
                    provider.GetRequiredService<CosmosRecoveryOptions>());
            });
            services.AddSingleton<IWorkerStateStore>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosWorkerStateStore(
                    context.System,
                    provider.GetRequiredService<IAppClock>());
            });
            services.AddSingleton<IExpirationPurger, CosmosExpirationPurger>();
            services.AddSingleton<IConsistencyRecoveryService>(provider =>
            {
                var context = provider.GetRequiredService<CosmosPersistenceContext>();
                return new CosmosConsistencyRecoveryService(
                    context.Lists,
                    context.Channels,
                    context.Recovery,
                    provider.GetRequiredService<IAppClock>(),
                    provider.GetRequiredService<CosmosRecoveryOptions>(),
                    provider.GetRequiredService<ILogger<CosmosConsistencyRecoveryService>>());
            });

            return services;
        }

        private static void ValidateCosmosOptions(CosmosOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString)
                && (string.IsNullOrWhiteSpace(options.Endpoint)
                    || string.IsNullOrWhiteSpace(options.Key)))
            {
                throw new InvalidOperationException(
                    "Cosmos persistence requires Cosmos:ConnectionString or both " +
                    "Cosmos:Endpoint and Cosmos:Key.");
            }

            if (string.IsNullOrWhiteSpace(options.DatabaseName))
            {
                throw new InvalidOperationException(
                    "Cosmos persistence requires Cosmos:DatabaseName.");
            }

            if (new[]
                {
                    options.ListsContainer,
                    options.ChannelsContainer,
                    options.ShareLinksContainer,
                    options.SystemContainer,
                    options.RecoveryContainer
                }.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    "Cosmos persistence requires non-empty names for all five containers.");
            }
        }
    }
}
