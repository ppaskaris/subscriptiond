using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace youtubed.Persistence.Cosmos
{
    public static class CosmosFoundationServiceCollectionExtensions
    {
        public static IServiceCollection AddCosmosFoundation(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(CosmosOptions.SectionName);
            services.Configure<CosmosOptions>(section);
            services.AddSingleton(section.Get<CosmosOptions>() ?? new CosmosOptions());
            services.AddSingleton(serviceProvider => CosmosClientFactory.Create(
                serviceProvider.GetRequiredService<CosmosOptions>()));
            services.AddSingleton<CosmosPersistenceContext>();
            services.AddSingleton<CosmosContainerInitializer>();
            return services;
        }
    }
}
