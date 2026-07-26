using System.Linq;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializerTests
    {
        [Fact]
        public void ListsPolicyIncludesMandatoryRootPath()
        {
            var lists = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.Lists);

            Assert.Contains(
                lists.IndexingPolicy.IncludedPaths,
                path => path.Path == "/*");
            Assert.Contains(
                lists.IndexingPolicy.ExcludedPaths,
                path => path.Path == "/channels/*");
        }

        [Fact]
        public void SystemPolicyReliesOnBuiltInIdIndexAndExcludesUserPaths()
        {
            var system = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.System);

            Assert.Empty(system.IndexingPolicy.IncludedPaths);
            Assert.Equal(
                new[] { "/*" },
                system.IndexingPolicy.ExcludedPaths.Select(path => path.Path));
        }

        [Fact]
        public void ChannelsPolicySupportsDeterministicStaleOrdering()
        {
            var channels = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions())
                .Single(container => container.Id == CosmosContainerNames.Channels);

            var composite = Assert.Single(
                channels.IndexingPolicy.CompositeIndexes,
                value => value.Count == 2 && value[0].Path == "/staleAfter");
            Assert.Equal(new[] { "/staleAfter", "/id" }, composite.Select(path => path.Path));
            Assert.All(composite, path => Assert.Equal(
                Microsoft.Azure.Cosmos.CompositePathSortOrder.Ascending,
                path.Order));
        }

        [Fact]
        public void RecoveryPoliciesExposeExactTotalOrderIndexes()
        {
            var properties = CosmosContainerInitializer
                .GetContainerProperties(new CosmosOptions());
            var lists = properties.Single(container => container.Id == CosmosContainerNames.Lists);
            var channels = properties.Single(container => container.Id == CosmosContainerNames.Channels);
            var recovery = properties.Single(container => container.Id == CosmosContainerNames.Recovery);

            Assert.Contains(
                lists.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/membershipRecoveryPending",
                    "/membershipRecoveryDueAt",
                    "/id"
                }));
            Assert.Contains(
                lists.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/membershipRecoveryDueAt",
                    "/id"
                }));
            Assert.Contains(
                channels.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/projectionRecoveryPending",
                    "/projectionRecoveryDueAt",
                    "/id"
                }));
            Assert.Contains(
                channels.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/projectionRecoveryDueAt",
                    "/id"
                }));
            Assert.Equal("/listId", recovery.PartitionKeyPath);
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/kind",
                    "/active",
                    "/nextAttemptAt",
                    "/listId",
                    "/id"
                }));
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/nextAttemptAt",
                    "/listId",
                    "/id"
                }));
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/kind",
                    "/nextCheckAt",
                    "/listId",
                    "/id"
                }));
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/nextCheckAt",
                    "/listId",
                    "/id"
                }));
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/kind",
                    "/active",
                    "/channelId",
                    "/id"
                }));
            Assert.Contains(
                recovery.IndexingPolicy.CompositeIndexes,
                index => index.Select(path => path.Path).SequenceEqual(new[]
                {
                    "/channelId",
                    "/id"
                }));
        }
    }
}
