using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    public sealed class RoutingWebApplicationFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var hostedServices = services
                    .Where(service =>
                        service.ServiceType == typeof(IHostedService) &&
                        (service.ImplementationType == typeof(MaintenanceHostedService) ||
                         service.ImplementationType == typeof(UpdateChannelHostedService)))
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
            });
        }
    }
}
