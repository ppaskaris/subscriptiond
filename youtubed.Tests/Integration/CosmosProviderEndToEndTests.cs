using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosProviderEndToEndTests
    {
        private readonly CosmosTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public CosmosProviderEndToEndTests(
            CosmosTestFixture fixture,
            ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [CosmosFact]
        public async Task ConfiguredDevelopmentHostStarts()
        {
            await using var factory = new CosmosWebApplicationFactory(CreateConfigurationValues());
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/");

            response.EnsureSuccessStatusCode();
        }

        [CosmosFact]
        public async Task ConfiguredProviderCompletesApplicationFlow()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var clock = new FakeAppClock
            {
                UtcNow = now,
                RandomDelayValue = TimeSpan.FromHours(1)
            };
            var channelUrl = "https://www.youtube.com/channel/end-to-end-channel";
            var youtube = new FakeYoutubeService();
            youtube.SetChannel(channelUrl, new YoutubeChannel
            {
                Id = "end-to-end-channel",
                Title = "End To End Channel",
                Thumbnail = "channel.png",
                PlaylistId = "end-to-end-playlist"
            });
            youtube.SetVideos(
                "end-to-end-playlist",
                new YoutubeVideo
                {
                    ChannelId = "end-to-end-channel",
                    Id = "end-to-end-video",
                    Title = "End To End Video",
                    Duration = TimeSpan.FromMinutes(4),
                    PublishedAt = now.AddMinutes(-10),
                    Thumbnail = "video.png"
                });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAppClock>(clock);
            services.AddSingleton<IYoutubeService>(youtube);
            services.AddSingleton<IYoutubeCallDelay, ImmediateYoutubeCallDelay>();
            services.AddSingleton<IWorkerWakeSignal, InProcessWorkerWakeSignal>();
            services.AddSingleton<IChannelUrlLookupCache, ChannelUrlLookupCache>();
            services.AddSingleton<IListService, ListService>();
            services.AddSingleton<IChannelService, ChannelService>();
            services.AddSingleton<IShareLinkService, ShareLinkService>();
            services.AddSingleton<IChannelRefreshPipeline, ChannelRefreshPipeline>();
            services.AddPersistence(CreateConfiguration());

            await using var provider = services.BuildServiceProvider();
            var initializer = provider.GetServices<IHostedService>()
                .OfType<CosmosPersistenceInitializerHostedService>()
                .Single();
            await initializer.StartAsync(CancellationToken.None);

            var listService = provider.GetRequiredService<IListService>();
            var channelService = provider.GetRequiredService<IChannelService>();
            var shareLinkService = provider.GetRequiredService<IShareLinkService>();
            var pipeline = provider.GetRequiredService<IChannelRefreshPipeline>();

            var list = await listService.CreateListAsync("Cosmos end to end");
            var channel = await channelService.GetOrCreateChannelAsync(channelUrl);
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                await listService.AddChannelAsync(list.Id, channel.Id);
                _output.WriteLine(
                    $"Membership add used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["membership_write"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }

            ChannelRefreshPipelineResult refresh;
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                refresh = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);
                _output.WriteLine(
                    $"One-channel refresh and projection fan-out used {scope.RequestCount} " +
                    $"requests and {scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["channel_refresh"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }
            var view = await listService.GetListViewAsync(list.Id);

            Assert.Equal(1, refresh.RefreshedChannelCount);
            Assert.Equal("End To End Video", Assert.Single(view.Videos).VideoTitle);

            var refreshedChannel = await provider.GetRequiredService<IChannelRepository>()
                .GetByIdAsync(channel.Id);
            refreshedChannel.Title = "Distinct fan-out update";
            await provider.GetRequiredService<IChannelRepository>()
                .SaveRefreshResultsAsync(
                    new[]
                    {
                        new ChannelRefreshResult
                        {
                            Channel = refreshedChannel,
                            VideosRefreshed = true,
                            EarliestPublishedAt = now.Subtract(Constants.VideoMaxAge)
                        }
                    },
                    CancellationToken.None);
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                await provider.GetRequiredService<IListProjectionRepository>()
                    .UpdateProjectedChannelsAsync(
                        new[] { refreshedChannel },
                        CancellationToken.None);
                _output.WriteLine(
                    $"Distinct one-list projection fan-out used {scope.RequestCount} requests " +
                    $"and {scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["projection_fan_out_per_list"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }

            ShareLinkModel shareLink;
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                shareLink = await shareLinkService.CreateShareLinkAsync(list.Id);
                _output.WriteLine(
                    $"Share create used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["share_operation"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }
            ConsumedShareLinkModel consumed;
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                consumed = await shareLinkService.ConsumeShareLinkAsync(shareLink.Password);
                _output.WriteLine(
                    $"Share consume used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["share_operation"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }
            Assert.Equal(list.Id, consumed.ListId);
            Assert.Equal(list.Token, consumed.Token);

            using (var scope = CosmosRequestChargeScope.Begin())
            {
                Assert.Single(await shareLinkService.GetShareLinksAsync(list.Id));
                _output.WriteLine(
                    $"Share list used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["share_operation"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }

            using (var scope = CosmosRequestChargeScope.Begin())
            {
                await provider.GetRequiredService<IWorkerStateStore>()
                    .ForceChannelRefreshAsync(CancellationToken.None);
                _output.WriteLine(
                    $"Scheduler force used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["scheduler_operation"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }

            using (var scope = CosmosRequestChargeScope.Begin())
            {
                await shareLinkService.DeleteShareLinkInListAsync(list.Id, shareLink.Password);
                _output.WriteLine(
                    $"Share delete used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["share_operation"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }
            Assert.Empty(await shareLinkService.GetShareLinksAsync(list.Id));

            var listRead = await _fixture
                .GetContainer(CosmosTestFixture.ListsContainerName)
                .ReadItemAsync<CosmosListDocument>(
                    list.Id.ToString("D"),
                    new PartitionKey(list.Id.ToString("D")));
            _output.WriteLine($"List point read consumed {listRead.RequestCharge:F2} RU.");
            Assert.True(listRead.RequestCharge > 0);

            using (var scope = CosmosRequestChargeScope.Begin())
            {
                await listService.RemoveChannelAsync(list.Id, channel.Id);
                _output.WriteLine(
                    $"Membership remove used {scope.RequestCount} requests and " +
                    $"{scope.RequestCharge:F2} emulator RU.");
                CosmosReleaseBudgets.AssertWithin(
                    CosmosReleaseBudgets.Operations["membership_write"],
                    scope.RequestCount,
                    scope.RequestCharge);
            }

            await listService.DeleteListAsync(list.Id);
            Assert.Null(await listService.GetListAsync(list.Id));

            var channelRead = await _fixture
                .GetContainer(CosmosTestFixture.ChannelsContainerName)
                .ReadItemAsync<CosmosChannelDocument>(
                    channel.Id,
                    new PartitionKey(channel.Id));
            Assert.Equal(0, channelRead.Resource.SubscriptionCount);
            Assert.Empty(channelRead.Resource.SubscribedListIds);
        }

        private IConfiguration CreateConfiguration()
        {
            var emulator = CosmosEmulatorOptions.FromEnvironment();
            return new ConfigurationBuilder()
                .AddInMemoryCollection(CreateConfigurationValues())
                .Build();
        }

        private IReadOnlyDictionary<string, string> CreateConfigurationValues()
        {
            var emulator = CosmosEmulatorOptions.FromEnvironment();
            return new Dictionary<string, string>
            {
                ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                ["Cosmos:ConnectionString"] = emulator.ConnectionString,
                ["Cosmos:DatabaseName"] = _fixture.DatabaseName,
                ["Cosmos:ListsContainer"] = CosmosTestFixture.ListsContainerName,
                ["Cosmos:ChannelsContainer"] = CosmosTestFixture.ChannelsContainerName,
                ["Cosmos:ShareLinksContainer"] = CosmosTestFixture.ShareLinksContainerName,
                ["Cosmos:SystemContainer"] = CosmosTestFixture.SystemContainerName
            };
        }

        private sealed class ImmediateYoutubeCallDelay : IYoutubeCallDelay
        {
            public Task DelayAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        private sealed class CosmosWebApplicationFactory : WebApplicationFactory<global::Program>
        {
            private readonly IReadOnlyDictionary<string, string> _configuration;

            public CosmosWebApplicationFactory(IReadOnlyDictionary<string, string> configuration)
            {
                _configuration = configuration;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(_configuration);
                });
                builder.ConfigureServices(services =>
                {
                    var worker = services.SingleOrDefault(service =>
                        service.ServiceType == typeof(IHostedService)
                        && service.ImplementationType == typeof(UnifiedWorkerHostedService));
                    if (worker != null)
                    {
                        services.Remove(worker);
                    }
                });
            }
        }
    }
}
