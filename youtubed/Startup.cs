using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using youtubed.Services;
using youtubed.Data;
using Microsoft.Extensions.Hosting;
using Dapper;

namespace youtubed
{
    public class Startup
    {
        public Startup(IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true); ;

            if (env.IsDevelopment())
            {
                builder.AddUserSecrets<Startup>();
            }

            Configuration = builder.Build();
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add
        // services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddOptions();
            services.AddMvc();

            services.Configure<YoutubeOptions>(Configuration.GetSection("Youtube"));

            services.AddSingleton<IChannelService, ChannelService>();
            services.AddSingleton<IChannelVideoService, ChannelVideoService>();
            services.AddSingleton<IConnectionFactory>(new ConnectionStringConnectionFactory(Configuration.GetConnectionString("Main")));
            services.AddSingleton<IHostedService, ChannelUpdaterHostedService>();
            services.AddSingleton<IListService, ListService>();
            services.AddSingleton<IYoutubeService, YoutubeService>();

            SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());
        }

        // This method gets called by the runtime. Use this method to configure
        // the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseStaticFiles();
            app.UseStatusCodePages();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
        }
    }
}
