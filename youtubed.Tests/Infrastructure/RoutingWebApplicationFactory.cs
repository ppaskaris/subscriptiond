using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using youtubed.Persistence.Cosmos;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    public sealed class RoutingWebApplicationFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(
                "Cosmos:ConnectionString",
                "AccountEndpoint=https://localhost:8081/;AccountKey=test-key;");
            builder.UseSetting("Cosmos:DatabaseName", "routing-tests");
            builder.ConfigureServices(services =>
            {
                var hostedServices = services
                    .Where(service =>
                        service.ServiceType == typeof(IHostedService) &&
                        (service.ImplementationType == typeof(ChannelRefreshHostedService) ||
                         service.ImplementationType == typeof(CosmosInitializationHostedService)))
                    .ToList();

                foreach (var hostedService in hostedServices)
                {
                    services.Remove(hostedService);
                }

                var listServiceRegistrations = services
                    .Where(service => service.ServiceType == typeof(IListService))
                    .ToList();

                foreach (var registration in listServiceRegistrations)
                {
                    services.Remove(registration);
                }

                var shareLinkServiceRegistrations = services
                    .Where(service => service.ServiceType == typeof(IShareLinkService))
                    .ToList();

                foreach (var registration in shareLinkServiceRegistrations)
                {
                    services.Remove(registration);
                }

                var channelServiceRegistrations = services
                    .Where(service => service.ServiceType == typeof(IChannelService))
                    .ToList();

                foreach (var registration in channelServiceRegistrations)
                {
                    services.Remove(registration);
                }

                services.PostConfigure<MvcOptions>(options =>
                {
                    var antiforgeryFilter = options.Filters
                        .OfType<AutoValidateAntiforgeryTokenAttribute>()
                        .FirstOrDefault();

                    if (antiforgeryFilter != null)
                    {
                        options.Filters.Remove(antiforgeryFilter);
                    }
                });

                services.AddSingleton<IListService, TestListService>();
                services.AddSingleton<IShareLinkService, TestShareLinkService>();
                services.AddSingleton(Mock.Of<IChannelService>());
            });
        }
    }
}
