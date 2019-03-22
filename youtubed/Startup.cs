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
using Microsoft.AspNetCore.Mvc;

namespace youtubed
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add
        // services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddOptions();
            services.AddMvc(options =>
            {
                options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });

            services.Configure<YoutubeOptions>(Configuration.GetSection("Youtube"));

            SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());

            services.AddSingleton<IConnectionFactory>(new ConnectionStringConnectionFactory(Configuration.GetConnectionString("Main")));
            services.AddSingleton<IYoutubeService, YoutubeService>();

            services.AddSingleton<IChannelService, ChannelService>();
            services.AddSingleton<IChannelVideoService, ChannelVideoService>();
            services.AddSingleton<IListService, ListService>();

            services.AddSingleton<IHostedService, MaintenanceHostedService>();
            services.AddSingleton<IHostedService, UpdateChannelHostedService>();
        }

        // This method gets called by the runtime. Use this method to configure
        // the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, Microsoft.AspNetCore.Hosting.IHostingEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
            }
            else
            {
                app.UseExceptionHandler("/error");
            }

            app.UseStaticFiles();
            app.UseStatusCodePagesWithRedirects("/error/{0}");
            app.UseMvc();
        }
    }
}
