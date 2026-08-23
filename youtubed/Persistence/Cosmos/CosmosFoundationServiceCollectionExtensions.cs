using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace youtubed.Persistence.Cosmos
{
    public static class CosmosFoundationServiceCollectionExtensions
    {
        public static IServiceCollection AddCosmosFoundation(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(CosmosOptions.SectionName);
            services.AddOptions<CosmosOptions>()
                .Bind(section)
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                    "Cosmos:ConnectionString is required.")
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.DatabaseName),
                    "Cosmos:DatabaseName is required.")
                .ValidateOnStart();
            services.AddSingleton(serviceProvider => CosmosClientFactory.Create(
                serviceProvider.GetRequiredService<IOptions<CosmosOptions>>().Value));
            services.AddSingleton<CosmosPersistenceContext>();
            services.AddSingleton<CosmosContainerInitializer>();
            services.AddSingleton<IHostedService, CosmosInitializationHostedService>();
            return services;
        }

    }
}
