using System.Threading.Tasks;
using System;
using Microsoft.Azure.Cosmos;
using Xunit;
using youtubed.Tests.Infrastructure;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosContainerSetupTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosContainerSetupTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task CreatesTtlEnabledContainersWithNarrowedIndexes()
        {
            var lists = (await _fixture.GetContainer(CosmosTestFixture.ListsContainerName).ReadContainerAsync()).Resource;
            var channels = (await _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName).ReadContainerAsync()).Resource;
            var shareLinks = (await _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName).ReadContainerAsync()).Resource;
            var system = (await _fixture.GetContainer(CosmosTestFixture.SystemContainerName).ReadContainerAsync()).Resource;
            var recovery = (await _fixture.GetContainer(CosmosTestFixture.RecoveryContainerName).ReadContainerAsync()).Resource;

            Assert.Equal(-1, lists.DefaultTimeToLive);
            Assert.Contains(lists.IndexingPolicy.ExcludedPaths, path => path.Path == "/channels/*");
            Assert.Equal(-1, channels.DefaultTimeToLive);
            Assert.Contains(channels.IndexingPolicy.IncludedPaths, path => path.Path == "/staleAfter/?");
            Assert.Contains(channels.IndexingPolicy.ExcludedPaths, path => path.Path == "/videos/*");
            Assert.Contains(
                channels.IndexingPolicy.CompositeIndexes,
                index => index.Count == 2
                    && index[0].Path == "/staleAfter"
                    && index[1].Path == "/id");
            Assert.Equal(-1, shareLinks.DefaultTimeToLive);
            Assert.Contains(shareLinks.IndexingPolicy.IncludedPaths, path => path.Path == "/listId/?");
            Assert.Null(system.DefaultTimeToLive);
            Assert.Empty(system.IndexingPolicy.IncludedPaths);
            Assert.Contains(system.IndexingPolicy.ExcludedPaths, path => path.Path == "/*");
            Assert.Equal("/listId", recovery.PartitionKeyPath);
            Assert.Null(recovery.DefaultTimeToLive);
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Count == 3
                    && index[0].Path == "/nextAttemptAt"
                    && index[1].Path == "/listId"
                    && index[2].Path == "/id");
        }

        [CosmosFact]
        public async Task UpgradesIndexPoliciesOnPreExistingContainers()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var options = new CosmosOptions
            {
                ListsContainer = $"legacy-lists-{suffix}",
                ChannelsContainer = $"legacy-channels-{suffix}",
                ShareLinksContainer = $"legacy-share-{suffix}",
                SystemContainer = $"legacy-system-{suffix}",
                RecoveryContainer = $"legacy-recovery-{suffix}"
            };
            await _fixture.Database.CreateContainerAsync(
                new ContainerProperties(options.ListsContainer, "/id"));
            await _fixture.Database.CreateContainerAsync(
                new ContainerProperties(options.ChannelsContainer, "/id"));
            await _fixture.Database.CreateContainerAsync(
                new ContainerProperties(options.RecoveryContainer, "/listId"));

            await new CosmosContainerInitializer().InitializeAsync(
                _fixture.Database,
                options);

            var lists = (await _fixture.Database
                .GetContainer(options.ListsContainer)
                .ReadContainerAsync()).Resource;
            var channels = (await _fixture.Database
                .GetContainer(options.ChannelsContainer)
                .ReadContainerAsync()).Resource;
            var recovery = (await _fixture.Database
                .GetContainer(options.RecoveryContainer)
                .ReadContainerAsync()).Resource;
            Assert.Contains(
                lists.IndexingPolicy.CompositeIndexes,
                index => index.Count == 2
                    && index[0].Path == "/membershipRecoveryDueAt"
                    && index[1].Path == "/id");
            Assert.Contains(
                channels.IndexingPolicy.CompositeIndexes,
                index => index.Count == 2
                    && index[0].Path == "/projectionRecoveryDueAt"
                    && index[1].Path == "/id");
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Count == 3
                    && index[0].Path == "/nextAttemptAt"
                    && index[1].Path == "/listId"
                    && index[2].Path == "/id");

            foreach (var containerName in new[]
            {
                options.ListsContainer,
                options.ChannelsContainer,
                options.ShareLinksContainer,
                options.SystemContainer,
                options.RecoveryContainer
            })
            {
                await _fixture.Database.GetContainer(containerName).DeleteContainerAsync();
            }
        }
    }
}
