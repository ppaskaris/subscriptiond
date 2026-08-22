using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using youtubed.Persistence.Cosmos;

namespace youtubed.Persistence
{
    public static class PersistenceServiceCollectionExtensions
    {
        public static IServiceCollection AddPersistence(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.AddCosmosFoundation(configuration);
            services.AddSingleton<IListRepository, CosmosListRepository>();
            services.AddSingleton<IShareLinkRepository, CosmosShareLinkRepository>();
            services.AddSingleton<IChannelRepository, CosmosChannelRepository>();
            return services;
        }
    }
}
