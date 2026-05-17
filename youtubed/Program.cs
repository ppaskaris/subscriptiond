using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using youtubed.Data;
using youtubed.DataTransfer;
using youtubed.Persistence;
using youtubed.Services;

if (DataTransferCli.IsDataTransferCommand(args))
{
    return await DataTransferCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});
builder.Services.AddLogging(loggingBuilder =>
{
    loggingBuilder.AddConsole();
    loggingBuilder.AddDebug();
    loggingBuilder.AddAzureWebAppDiagnostics();
});

builder.Services.Configure<YoutubeOptions>(builder.Configuration.GetSection("Youtube"));

SqlMapper.AddTypeHandler(new TimeSpanTypeHandler());

builder.Services.AddSingleton<IConnectionFactory>(
    new ConnectionStringConnectionFactory(builder.Configuration.GetConnectionString("Main")));
builder.Services.AddSingleton<IYoutubeService, YoutubeService>();
builder.Services.AddSingleton<IListRepository, ListRepository>();
builder.Services.AddSingleton<IShareLinkRepository, ShareLinkRepository>();
builder.Services.AddSingleton<IChannelRepository, ChannelRepository>();
builder.Services.AddSingleton<IChannelVideoRepository, ChannelVideoRepository>();

builder.Services.AddSingleton<IAppClock, AppClock>();
builder.Services.AddSingleton<IChannelUrlLookupCache, ChannelUrlLookupCache>();
builder.Services.AddSingleton<IChannelService, ChannelService>();
builder.Services.AddSingleton<IChannelVideoService, ChannelVideoService>();
builder.Services.AddSingleton<IListService, ListService>();
builder.Services.AddSingleton<IShareLinkService, ShareLinkService>();

builder.Services.AddSingleton<IHostedService, MaintenanceHostedService>();
builder.Services.AddSingleton<IHostedService, UpdateChannelHostedService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseStaticFiles();
app.UseStatusCodePagesWithRedirects("/error/{0}");
app.UseRouting();

app.MapControllers();

app.Run();
return 0;

public partial class Program
{
}
