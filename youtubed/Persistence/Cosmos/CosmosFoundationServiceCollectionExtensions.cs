using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace youtubed.Persistence.Cosmos
{
    public static class CosmosFoundationServiceCollectionExtensions
    {
        public static IServiceCollection AddCosmosFoundation(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var section = configuration.GetSection(CosmosOptions.SectionName);
            var options = section.Get<CosmosOptions>() ?? new CosmosOptions();
            ValidateOptions(options);
            services.Configure<CosmosOptions>(section);
            services.AddSingleton(options);
            services.AddSingleton(serviceProvider => CosmosClientFactory.Create(
                serviceProvider.GetRequiredService<CosmosOptions>()));
            services.AddSingleton<CosmosPersistenceContext>();
            services.AddSingleton<CosmosContainerInitializer>();
            services.AddSingleton<IHostedService, CosmosInitializationHostedService>();
            return services;
        }

        internal static void ValidateOptions(CosmosOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Cosmos:ConnectionString is required.");
            }

            if (string.IsNullOrWhiteSpace(options.DatabaseName))
            {
                throw new InvalidOperationException(
                    "Cosmos:DatabaseName is required.");
            }
        }
    }
}
