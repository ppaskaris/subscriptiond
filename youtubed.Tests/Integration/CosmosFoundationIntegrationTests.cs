using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Xunit;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosFoundationIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosFoundationIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task DevelopmentSetupCreatesExactlyThreeExpectedContainers()
        {
            var containers = new List<ContainerProperties>();
            using var iterator = _fixture.Context.Database
                .GetContainerQueryIterator<ContainerProperties>();
            while (iterator.HasMoreResults)
            {
                containers.AddRange(await iterator.ReadNextAsync());
            }

            Assert.Equal(
                new[]
                {
                    CosmosContainerNames.Channels,
                    CosmosContainerNames.Lists,
                    CosmosContainerNames.ShareLinks
                },
                containers.Select(container => container.Id).OrderBy(id => id, StringComparer.Ordinal));
            foreach (var expected in CosmosContainerInitializer.GetContainerProperties())
            {
                var actual = (await _fixture.Context.Database
                    .GetContainer(expected.Id)
                    .ReadContainerAsync()).Resource;
                CosmosContainerInitializer.ValidateContainerProperties(actual, expected);
            }
        }

        [CosmosFact]
        public async Task ProductionSetupDoesNotCreateAMissingDatabase()
        {
            var databaseName = $"missing-{Guid.NewGuid():N}";
            var options = new CosmosOptions { DatabaseName = databaseName };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new CosmosContainerInitializer().InitializeProductionAsync(
                    _fixture.Client,
                    options));

            Assert.Contains("must be provisioned", exception.Message);
            Assert.DoesNotContain(databaseName, exception.Message);
            Assert.Null(exception.InnerException);
            await Assert.ThrowsAsync<CosmosException>(() =>
                _fixture.Client.GetDatabase(databaseName).ReadAsync());
        }

        [CosmosFact]
        public async Task ProductionSetupDoesNotExposeAMissingContainerRequest()
        {
            var databaseName = $"missing-container-{Guid.NewGuid():N}";
            var database = (await _fixture.Client.CreateDatabaseAsync(
                databaseName,
                CosmosContainerInitializer.SharedDatabaseThroughput)).Database;
            try
            {
                var expected = CosmosContainerInitializer.GetContainerProperties();
                await database.CreateContainerAsync(expected[0]);
                await database.CreateContainerAsync(expected[1]);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CosmosContainerInitializer().InitializeProductionAsync(
                        _fixture.Client,
                        new CosmosOptions { DatabaseName = databaseName }));

                Assert.Contains("required Cosmos container", exception.Message);
                Assert.DoesNotContain(databaseName, exception.Message);
                Assert.DoesNotContain(CosmosContainerNames.ShareLinks, exception.Message);
                Assert.Null(exception.InnerException);
            }
            finally
            {
                await database.DeleteAsync();
            }
        }

        [CosmosFact]
        public async Task ProductionSetupDetectsPartitionTtlAndIndexDrift()
        {
            await AssertDriftAsync(
                CosmosContainerNames.Lists,
                expected => new ContainerProperties(expected.Id, "/wrong"));
            await AssertDriftAsync(
                CosmosContainerNames.Lists,
                expected => Clone(expected, defaultTimeToLive: null));
            await AssertDriftAsync(
                CosmosContainerNames.Channels,
                expected => Clone(expected, omitExcludedPaths: true));
            await AssertDriftAsync(
                CosmosContainerNames.Channels,
                expected =>
                {
                    var changed = Clone(expected);
                    var spatialPath = new SpatialPath { Path = "/location/*" };
                    spatialPath.SpatialTypes.Add(SpatialType.Point);
                    changed.IndexingPolicy.SpatialIndexes.Add(spatialPath);
                    return changed;
                });
        }

        [CosmosFact]
        public async Task ProductionSetupRejectsDedicatedContainerThroughput()
        {
            var databaseName = $"dedicated-{Guid.NewGuid():N}";
            var database = (await _fixture.Client.CreateDatabaseAsync(
                databaseName,
                CosmosContainerInitializer.SharedDatabaseThroughput)).Database;
            try
            {
                foreach (var expected in CosmosContainerInitializer.GetContainerProperties())
                {
                    if (expected.Id == CosmosContainerNames.Lists)
                    {
                        await database.CreateContainerAsync(expected, throughput: 400);
                    }
                    else
                    {
                        await database.CreateContainerAsync(expected);
                    }
                }

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CosmosContainerInitializer().InitializeProductionAsync(
                        _fixture.Client,
                        new CosmosOptions { DatabaseName = databaseName }));
                Assert.Contains("inherit shared", exception.Message);
            }
            finally
            {
                await database.DeleteAsync();
            }
        }

        [CosmosFact]
        public async Task MaximumDocumentsRemainBoundedAndHaveRepresentativeRequestCharges()
        {
            var now = DateTimeOffset.UtcNow;
            var list = new CosmosListDocument
            {
                Id = Guid.NewGuid().ToString("D"),
                Token = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray(),
                Title = new string('l', 200),
                PlaybackRate = 1m,
                ExpiredAfter = now.AddDays(46),
                ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime),
                ChannelIds = Enumerable.Range(0, 100)
                    .Select(value => $"UC{new string('x', 20)}{value:D3}")
                    .ToArray(),
                Ttl = (int)TimeSpan.FromDays(46).TotalSeconds
            };
            var channel = new CosmosChannelDocument
            {
                Id = $"UC-{Guid.NewGuid():N}",
                Url = "https://www.youtube.com/channel/test",
                Title = new string('c', 200),
                Thumbnail = "https://example.test/channel.jpg",
                PlaylistId = "UU-test",
                StaleAfter = now.AddHours(1),
                Status = "Active",
                Videos = Enumerable.Range(0, 100).Select(value => new CosmosVideoDocument
                {
                    Id = $"video-{value:D3}",
                    Title = new string('v', 200),
                    DurationTicks = TimeSpan.FromMinutes(3).Ticks,
                    PublishedAt = now.AddMinutes(-value),
                    Thumbnail = $"https://example.test/video-{value:D3}.jpg"
                }).ToArray()
            };

            var listSize = CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(list);
            var channelSize = CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(channel);
            var listResponse = await _fixture.Context.Lists.CreateItemAsync(
                list,
                new PartitionKey(list.Id));
            var channelResponse = await _fixture.Context.Channels.CreateItemAsync(
                channel,
                new PartitionKey(channel.Id));

            Assert.InRange(listSize, 1, 32 * 1024);
            Assert.InRange(channelSize, 1, 256 * 1024);
            Assert.True(listResponse.RequestCharge > 0);
            Assert.True(channelResponse.RequestCharge > 0);
        }

        [CosmosFact]
        public async Task ListTtlDeletesOnlyTheListAndLeavesUnreferencedChannelInert()
        {
            var id = Guid.NewGuid().ToString("D");
            var channelId = $"UC-{Guid.NewGuid():N}";
            await _fixture.Context.Channels.CreateItemAsync(
                new CosmosChannelDocument
                {
                    Id = channelId,
                    Status = "Active",
                    Videos = Array.Empty<CosmosVideoDocument>()
                },
                new PartitionKey(channelId));
            await _fixture.Context.Lists.CreateItemAsync(
                new CosmosListDocument
                {
                    Id = id,
                    Token = new byte[] { 1, 2, 3 },
                    ChannelIds = new[] { channelId },
                    Ttl = 1
                },
                new PartitionKey(id));

            var deleted = false;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (!deleted && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                try
                {
                    await _fixture.Context.Lists.ReadItemAsync<CosmosListDocument>(
                        id,
                        new PartitionKey(id));
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    deleted = true;
                }
            }

            Assert.True(deleted, "The emulator did not physically delete the TTL-expired list within three minutes.");
            var channel = await _fixture.Context.Channels.ReadItemAsync<CosmosChannelDocument>(
                channelId,
                new PartitionKey(channelId));
            Assert.Equal(channelId, channel.Resource.Id);
        }

        private async Task AssertDriftAsync(
            string changedContainer,
            Func<ContainerProperties, ContainerProperties> mutate)
        {
            var databaseName = $"drift-{Guid.NewGuid():N}";
            var database = (await _fixture.Client.CreateDatabaseAsync(
                databaseName,
                CosmosContainerInitializer.SharedDatabaseThroughput)).Database;
            try
            {
                foreach (var expected in CosmosContainerInitializer.GetContainerProperties())
                {
                    await database.CreateContainerAsync(
                        expected.Id == changedContainer ? mutate(expected) : expected);
                }

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new CosmosContainerInitializer().InitializeProductionAsync(
                        _fixture.Client,
                        new CosmosOptions { DatabaseName = databaseName }));
            }
            finally
            {
                await database.DeleteAsync();
            }
        }

        private static ContainerProperties Clone(
            ContainerProperties source,
            int? defaultTimeToLive = null,
            bool omitExcludedPaths = false)
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

            if (!omitExcludedPaths)
            {
                foreach (var path in source.IndexingPolicy.ExcludedPaths)
                {
                    clone.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = path.Path });
                }
            }

            return clone;
        }
    }
}
