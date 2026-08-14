using System;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using youtubed.Data;

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
                PersistenceProvider.Cosmos => throw new InvalidOperationException(
                    "Cosmos persistence is temporarily unavailable while the simplified " +
                    "provider is being rebuilt. Select 'SqlServer' with Persistence:Provider."),
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
    }
}
