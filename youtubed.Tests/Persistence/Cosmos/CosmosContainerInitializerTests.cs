using System;
using System.Linq;
using Microsoft.Azure.Cosmos;
using Xunit;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializerTests
    {
        [Fact]
        public void DefinesExactlyThreePointPartitionedContainers()
        {
            var properties = CosmosContainerInitializer.GetContainerProperties();

            Assert.Equal(
                new[]
                {
                    CosmosContainerNames.Lists,
                    CosmosContainerNames.Channels,
                    CosmosContainerNames.ShareLinks
                },
                properties.Select(container => container.Id));
            Assert.All(properties, container => Assert.Equal("/id", container.PartitionKeyPath));
            Assert.All(properties, container => Assert.Empty(container.IndexingPolicy.CompositeIndexes));
            Assert.All(properties, container => Assert.Empty(container.IndexingPolicy.SpatialIndexes));
            Assert.All(properties, container => Assert.Empty(container.IndexingPolicy.VectorIndexes));
            Assert.All(properties, container => Assert.Empty(container.IndexingPolicy.FullTextIndexes));
            Assert.All(properties, container => Assert.Null(container.VectorEmbeddingPolicy));
            Assert.All(properties, container => Assert.Null(container.FullTextPolicy));
        }

        [Fact]
        public void PoliciesEnableOnlyRequiredTtlAndExcludeSecretsAndVideos()
        {
            var properties = CosmosContainerInitializer.GetContainerProperties();
            var lists = properties.Single(value => value.Id == CosmosContainerNames.Lists);
            var channels = properties.Single(value => value.Id == CosmosContainerNames.Channels);
            var shareLinks = properties.Single(value => value.Id == CosmosContainerNames.ShareLinks);

            Assert.Equal(-1, lists.DefaultTimeToLive);
            Assert.Contains(lists.IndexingPolicy.ExcludedPaths, path => path.Path == "/token/?");
            Assert.Null(channels.DefaultTimeToLive);
            Assert.Contains(channels.IndexingPolicy.ExcludedPaths, path => path.Path == "/videos/*");
            Assert.Equal(-1, shareLinks.DefaultTimeToLive);
            Assert.Equal(
                new[] { "/createdAt/?", "/expiresAfter/?", "/listId/?", "/usedAt/?" },
                shareLinks.IndexingPolicy.IncludedPaths
                    .Select(path => path.Path)
                    .OrderBy(path => path, StringComparer.Ordinal));
            Assert.Contains(shareLinks.IndexingPolicy.ExcludedPaths, path => path.Path == "/*");
        }

        [Fact]
        public void DriftValidationRejectsWrongPartitionKeyTtlAndIndexing()
        {
            var expected = CosmosContainerInitializer.GetContainerProperties()[0];
            var wrongPartition = Clone(expected);
            wrongPartition.PartitionKeyPath = "/listId";
            var wrongTtl = Clone(expected);
            wrongTtl.DefaultTimeToLive = null;
            var wrongIndex = Clone(expected);
            wrongIndex.IndexingPolicy.ExcludedPaths.Clear();

            Assert.Contains("partition key", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(wrongPartition, expected)).Message);
            Assert.Contains("TTL", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(wrongTtl, expected)).Message);
            Assert.Contains("indexing", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(wrongIndex, expected)).Message);
        }

        [Fact]
        public void DriftValidationRejectsSpatialVectorAndFullTextIndexes()
        {
            var expected = CosmosContainerInitializer.GetContainerProperties()[1];
            var spatial = Clone(expected);
            spatial.IndexingPolicy.SpatialIndexes.Add(CreateSpatialPath());
            var vector = Clone(expected);
            vector.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath
            {
                Path = "/embedding",
                Type = VectorIndexType.Flat
            });
            var fullText = Clone(expected);
            fullText.IndexingPolicy.FullTextIndexes.Add(new FullTextIndexPath { Path = "/title" });

            Assert.Contains("indexing", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(spatial, expected)).Message);
            Assert.Contains("indexing", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(vector, expected)).Message);
            Assert.Contains("indexing", Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerProperties(fullText, expected)).Message);
        }

        [Fact]
        public void ProductionRequiresManualSharedDatabaseThroughput()
        {
            CosmosContainerInitializer.ValidateDatabaseThroughput(
                ThroughputProperties.CreateManualThroughput(1000));
            CosmosContainerInitializer.ValidateContainerThroughput(CosmosContainerNames.Lists, null);

            Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateDatabaseThroughput(
                    ThroughputProperties.CreateManualThroughput(400)));
            Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateDatabaseThroughput(
                    ThroughputProperties.CreateAutoscaleThroughput(1000)));
            Assert.Throws<InvalidOperationException>(() =>
                CosmosContainerInitializer.ValidateContainerThroughput(
                    CosmosContainerNames.Lists,
                    400));
        }

        [Fact]
        public void ClientUsesTheSameSerializerAsSizeTests()
        {
            var options = CosmosClientFactory.CreateClientOptions();

            Assert.Same(CosmosSystemTextJsonSerializer.Instance, options.Serializer);
        }

        private static ContainerProperties Clone(ContainerProperties source)
        {
            var clone = new ContainerProperties(source.Id, source.PartitionKeyPath)
            {
                DefaultTimeToLive = source.DefaultTimeToLive,
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

        private static SpatialPath CreateSpatialPath()
        {
            var path = new SpatialPath { Path = "/location/*" };
            path.SpatialTypes.Add(SpatialType.Point);
            return path;
        }
    }
}
