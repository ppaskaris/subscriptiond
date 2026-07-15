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
                    "The Cosmos persistence provider is not implemented. " +
                    "Set Persistence:Provider to SqlServer."),
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
            services.AddSingleton<IChannelVideoRepository, ChannelVideoRepository>();
            services.AddSingleton<IWorkerStateStore, WorkerStateRepository>();
            services.AddSingleton<IExpirationPurger, SqlExpirationPurger>();
            services.AddSingleton<IListProjectionRepository, SqlListProjectionRepository>();

            return services;
        }
    }
}
