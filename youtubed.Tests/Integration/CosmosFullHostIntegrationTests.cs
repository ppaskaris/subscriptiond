using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosFullHostIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosFullHostIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task AnonymousApplicationFlowRunsThroughCosmosHost()
        {
            var clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
                RandomDelayValue = TimeSpan.FromHours(1)
            };
            var youtube = new FakeYoutubeService();
            const string channelId = "UC-full-host";
            const string playlistId = "UU-full-host";
            const string submittedUrl = "https://www.youtube.com/channel/UC-full-host";
            youtube.SetChannel(submittedUrl, new YoutubeChannel
            {
                Id = channelId,
                Title = "Host Channel",
                Thumbnail = "https://example.test/channel.jpg",
                PlaylistId = playlistId
            });
            youtube.SetVideos(playlistId, new YoutubeVideo
            {
                ChannelId = channelId,
                Id = "host-video",
                Title = "Host Video",
                Duration = TimeSpan.FromMinutes(5),
                PublishedAt = clock.UtcNow.AddMinutes(-10),
                Thumbnail = "https://example.test/video.jpg"
            });

            using var factory = new CosmosWebApplicationFactory(
                CosmosEmulatorOptions.FromEnvironment().ConnectionString,
                _fixture.DatabaseName,
                clock,
                youtube);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            Assert.IsType<CosmosListRepository>(
                factory.Services.GetRequiredService<IListRepository>());
            Assert.IsType<CosmosChannelRepository>(
                factory.Services.GetRequiredService<IChannelRepository>());
            Assert.IsType<CosmosShareLinkRepository>(
                factory.Services.GetRequiredService<IShareLinkRepository>());
            Assert.IsType<CosmosExpirationPurger>(
                factory.Services.GetRequiredService<IExpirationPurger>());

            using var createResponse = await client.PostAsync(
                "/create-list",
                Form(("Title", "Cosmos Host List")));
            Assert.Equal(HttpStatusCode.Redirect, createResponse.StatusCode);
            var listPath = createResponse.Headers.Location?.OriginalString;
            Assert.NotNull(listPath);
            var segments = listPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, segments.Length);
            var token = segments[0];
            var listId = Guid.Parse(segments[2]);

            using var authenticateResponse = await client.GetAsync(listPath);
            Assert.Equal(HttpStatusCode.OK, authenticateResponse.StatusCode);
            var persistedList = await factory.Services
                .GetRequiredService<IListRepository>()
                .GetAsync(listId);
            Assert.Equal(clock.UtcToday, persistedList.ExpirationRenewedOn);

            using var addResponse = await client.PostAsync(
                $"{listPath}/add-channel",
                Form(("Url", submittedUrl)));
            Assert.True(
                addResponse.StatusCode == HttpStatusCode.Redirect,
                $"Add channel returned {(int)addResponse.StatusCode}: " +
                await addResponse.Content.ReadAsStringAsync());

            var renderedVideo = false;
            var refreshDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!renderedVideo && DateTimeOffset.UtcNow < refreshDeadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                using var listResponse = await client.GetAsync(listPath);
                var content = await listResponse.Content.ReadAsStringAsync();
                renderedVideo = content.Contains("Host Video", StringComparison.Ordinal);
            }
            Assert.True(renderedVideo, "The request-driven refresh did not render the expected video.");

            using var createShareResponse = await client.PostAsync(
                $"{listPath}/share/create",
                new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));
            Assert.Equal(HttpStatusCode.Redirect, createShareResponse.StatusCode);
            var shareRepository = factory.Services.GetRequiredService<IShareLinkRepository>();
            var share = Assert.Single(await shareRepository.GetByListAsync(listId));

            using var consumeResponse = await client.GetAsync($"/share/{share.Password}");
            Assert.Equal(HttpStatusCode.Redirect, consumeResponse.StatusCode);
            Assert.Equal(listPath, consumeResponse.Headers.Location?.OriginalString);

            using var deleteShareResponse = await client.PostAsync(
                $"{listPath}/share/delete",
                Form(("password", share.Password)));
            Assert.Equal(HttpStatusCode.Redirect, deleteShareResponse.StatusCode);
            Assert.Empty(await shareRepository.GetByListAsync(listId));

            using var removeResponse = await client.PostAsync(
                $"{listPath}/remove-channel",
                Form(("ChannelId", channelId)));
            Assert.Equal(HttpStatusCode.Redirect, removeResponse.StatusCode);
            var channelView = await factory.Services
                .GetRequiredService<IListRepository>()
                .GetChannelProjectionAsync(persistedList);
            Assert.Empty(channelView.ChannelIds);

            using var deleteResponse = await client.PostAsync(
                $"{listPath}/delete",
                Form(("Confirm", "true")));
            Assert.Equal(HttpStatusCode.Redirect, deleteResponse.StatusCode);
            Assert.Null(await factory.Services.GetRequiredService<IListRepository>().GetAsync(listId));
        }

        [CosmosFact]
        public void ProductionHostFailsEarlyForMissingCredentialsWithoutExposingConfiguration()
        {
            const string databaseName = "missing-credentials-database";
            using var factory = new ConfiguredWebApplicationFactory(
                Environments.Production,
                new Dictionary<string, string>
                {
                    ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                    ["Cosmos:DatabaseName"] = databaseName,
                    ["Cosmos:ConnectionString"] = " "
                });

            var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("Cosmos:ConnectionString", GetExceptionMessages(exception));
            Assert.DoesNotContain(databaseName, GetExceptionMessages(exception));
            AssertSafeStartupException(exception);
        }

        [CosmosFact]
        public void ProductionHostSanitizesMissingDatabaseFailure()
        {
            var databaseName = $"host-missing-{Guid.NewGuid():N}";
            var connectionString = CosmosEmulatorOptions.FromEnvironment().ConnectionString;
            using var factory = CreateProductionFactory(connectionString, databaseName);

            var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains(
                "configured Cosmos database must be provisioned",
                GetExceptionMessages(exception));
            Assert.DoesNotContain(databaseName, GetExceptionMessages(exception));
            AssertSafeStartupException(exception);
        }

        [CosmosFact]
        public async Task ProductionHostRejectsPolicyDriftWithSafeMessage()
        {
            var databaseName = $"host-drift-{Guid.NewGuid():N}";
            var database = (await _fixture.Client.CreateDatabaseAsync(
                databaseName,
                CosmosContainerInitializer.SharedDatabaseThroughput)).Database;
            try
            {
                foreach (var expected in CosmosContainerInitializer.GetContainerProperties())
                {
                    var properties = expected;
                    if (expected.Id == CosmosContainerNames.Lists)
                    {
                        properties = CloneWithTtl(expected, defaultTimeToLive: null);
                    }

                    await database.CreateContainerAsync(properties);
                }

                var connectionString = CosmosEmulatorOptions.FromEnvironment().ConnectionString;
                using var factory = CreateProductionFactory(connectionString, databaseName);
                var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

                Assert.Contains("unexpected TTL configuration", GetExceptionMessages(exception));
                Assert.DoesNotContain(databaseName, GetExceptionMessages(exception));
                AssertSafeStartupException(exception);
            }
            finally
            {
                await database.DeleteAsync();
            }
        }

        private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        {
            return new FormUrlEncodedContent(values.Select(value =>
                new KeyValuePair<string, string>(value.Key, value.Value)));
        }

        private static ConfiguredWebApplicationFactory CreateProductionFactory(
            string connectionString,
            string databaseName)
        {
            return new ConfiguredWebApplicationFactory(
                Environments.Production,
                new Dictionary<string, string>
                {
                    ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                    ["Cosmos:ConnectionString"] = connectionString,
                    ["Cosmos:DatabaseName"] = databaseName
                });
        }

        private static ContainerProperties CloneWithTtl(
            ContainerProperties source,
            int? defaultTimeToLive)
        {
            var clone = new ContainerProperties(source.Id, source.PartitionKeyPath)
            {
                DefaultTimeToLive = defaultTimeToLive,
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = source.IndexingPolicy.Automatic,
                    IndexingMode = source.IndexingPolicy.IndexingMode
                }
            };
            foreach (var path in source.IndexingPolicy.IncludedPaths)
            {
                clone.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path.Path });
            }

            foreach (var path in source.IndexingPolicy.ExcludedPaths)
            {
                clone.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = path.Path });
            }

            return clone;
        }

        private static string GetExceptionMessages(Exception exception)
        {
            return string.Join(" | ", GetExceptions(exception).Select(value => value.Message));
        }

        private static void AssertSafeStartupException(Exception exception)
        {
            var exceptions = GetExceptions(exception).ToArray();
            var messages = string.Join(" | ", exceptions.Select(value => value.Message));
            Assert.DoesNotContain(exceptions, value => value is CosmosException);
            Assert.DoesNotContain("AccountKey", messages, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localhost:8081", messages, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RequestUri", messages, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Diagnostics", messages, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<Exception> GetExceptions(Exception exception)
        {
            yield return exception;
            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(GetExceptions))
                {
                    yield return inner;
                }

                yield break;
            }

            if (exception.InnerException != null)
            {
                foreach (var inner in GetExceptions(exception.InnerException))
                {
                    yield return inner;
                }
            }
        }

        private sealed class CosmosWebApplicationFactory : ConfiguredWebApplicationFactory
        {
            private readonly FakeAppClock _clock;
            private readonly FakeYoutubeService _youtube;

            public CosmosWebApplicationFactory(
                string connectionString,
                string databaseName,
                FakeAppClock clock,
                FakeYoutubeService youtube)
                : base(
                    Environments.Development,
                    new Dictionary<string, string>
                    {
                        ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                        ["Cosmos:ConnectionString"] = connectionString,
                        ["Cosmos:DatabaseName"] = databaseName
                    })
            {
                _clock = clock;
                _youtube = youtube;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IYoutubeService>();
                    services.RemoveAll<IAppClock>();
                    services.RemoveAll<IYoutubeCallDelay>();
                    var maintenance = services
                        .Where(service => service.ServiceType == typeof(IHostedService)
                            && service.ImplementationType == typeof(MaintenanceHostedService))
                        .ToArray();
                    foreach (var registration in maintenance)
                    {
                        services.Remove(registration);
                    }

                    services.PostConfigure<MvcOptions>(options =>
                    {
                        var antiforgery = options.Filters
                            .OfType<AutoValidateAntiforgeryTokenAttribute>()
                            .Single();
                        options.Filters.Remove(antiforgery);
                    });
                    services.AddSingleton<IYoutubeService>(_youtube);
                    services.AddSingleton<IAppClock>(_clock);
                    services.AddSingleton<IYoutubeCallDelay, ImmediateYoutubeCallDelay>();
                });
            }
        }

        private class ConfiguredWebApplicationFactory : WebApplicationFactory<global::Program>
        {
            private readonly string _environment;
            private readonly IReadOnlyDictionary<string, string> _settings;
            private readonly Dictionary<string, string> _originalEnvironment = new();
            private bool _environmentRestored;

            public ConfiguredWebApplicationFactory(
                string environment,
                IReadOnlyDictionary<string, string> settings)
            {
                _environment = environment;
                _settings = settings;
                SetEnvironment("ASPNETCORE_ENVIRONMENT", environment);
                SetEnvironment("DOTNET_ENVIRONMENT", environment);
                foreach (var setting in settings)
                {
                    SetEnvironment(setting.Key.Replace(":", "__", StringComparison.Ordinal), setting.Value);
                }
            }

            protected override IHost CreateHost(IHostBuilder builder)
            {
                try
                {
                    return base.CreateHost(builder);
                }
                finally
                {
                    RestoreEnvironment();
                }
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment(_environment);
            }

            protected override void Dispose(bool disposing)
            {
                RestoreEnvironment();
                base.Dispose(disposing);
            }

            private void SetEnvironment(string name, string value)
            {
                _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            private void RestoreEnvironment()
            {
                if (_environmentRestored)
                {
                    return;
                }

                foreach (var value in _originalEnvironment)
                {
                    Environment.SetEnvironmentVariable(value.Key, value.Value);
                }

                _environmentRestored = true;
            }
        }

        private sealed class ImmediateYoutubeCallDelay : IYoutubeCallDelay
        {
            public Task DelayAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
