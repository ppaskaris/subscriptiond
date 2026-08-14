using System;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using youtubed.Data;
using youtubed.Persistence.Cosmos;

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

        public static IServiceCollection AddCosmosPersistence(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddCosmosFoundation(configuration);
            services.AddSingleton<IListRepository, CosmosListRepository>();
            services.AddSingleton<IShareLinkRepository, CosmosShareLinkRepository>();
            services.AddSingleton<IChannelRepository, CosmosChannelRepository>();
            services.AddSingleton<IExpirationPurger, CosmosExpirationPurger>();

            return services;
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
            services.AddSingleton<IExpirationPurger, SqlExpirationPurger>();

            return services;
        }
    }
}
