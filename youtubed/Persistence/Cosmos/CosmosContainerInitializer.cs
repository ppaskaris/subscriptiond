using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializer
    {
        public const int SharedDatabaseThroughput = 1000;
        private const string IdPartitionKeyPath = "/id";

        public async Task<CosmosPersistenceContext> InitializeDevelopmentAsync(
            CosmosClient client,
            CosmosOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);

            var response = await client.CreateDatabaseIfNotExistsAsync(
                options.DatabaseName,
                SharedDatabaseThroughput,
                cancellationToken: cancellationToken);
            foreach (var properties in GetContainerProperties())
            {
                await response.Database.CreateContainerIfNotExistsAsync(
                    properties,
                    cancellationToken: cancellationToken);
            }

            await ValidateAsync(response.Database, cancellationToken);
            return new CosmosPersistenceContext(client, options);
        }

        public async Task<CosmosPersistenceContext> InitializeProductionAsync(
            CosmosClient client,
            CosmosOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(options);

            var database = client.GetDatabase(options.DatabaseName);
            try
            {
                await database.ReadAsync(cancellationToken: cancellationToken);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    "The configured Cosmos database must be provisioned before production startup.");
            }

            await ValidateAsync(database, cancellationToken);
            return new CosmosPersistenceContext(client, options);
        }

        public static IReadOnlyList<ContainerProperties> GetContainerProperties()
        {
            return new[]
            {
                CreateListsProperties(),
                CreateChannelsProperties(),
                CreateShareLinksProperties()
            };
        }

        internal static void ValidateDatabaseThroughput(ThroughputProperties throughput)
        {
            if (throughput?.Throughput != SharedDatabaseThroughput
                || throughput.AutoscaleMaxThroughput.HasValue)
            {
                throw new InvalidOperationException(
                    "Cosmos production requires exactly 1,000 RU/s manual shared database throughput.");
            }
        }

        internal static void ValidateContainerProperties(
            ContainerProperties actual,
            ContainerProperties expected)
        {
            if (!string.Equals(actual.PartitionKeyPath, expected.PartitionKeyPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cosmos container '{expected.Id}' has partition key '{actual.PartitionKeyPath}', " +
                    $"expected '{expected.PartitionKeyPath}'.");
            }

            if (actual.DefaultTimeToLive != expected.DefaultTimeToLive)
            {
                throw new InvalidOperationException(
                    $"Cosmos container '{expected.Id}' has unexpected TTL configuration.");
            }

            if (actual.IndexingPolicy.Automatic != expected.IndexingPolicy.Automatic
                || actual.IndexingPolicy.IndexingMode != expected.IndexingPolicy.IndexingMode
                || !GetPaths(actual.IndexingPolicy.IncludedPaths)
                    .SequenceEqual(GetPaths(expected.IndexingPolicy.IncludedPaths))
                || !GetPaths(actual.IndexingPolicy.ExcludedPaths)
                    .SequenceEqual(GetPaths(expected.IndexingPolicy.ExcludedPaths))
                || actual.IndexingPolicy.CompositeIndexes.Count != 0
                || actual.IndexingPolicy.SpatialIndexes.Count != 0
                || actual.IndexingPolicy.VectorIndexes.Count != 0
                || actual.IndexingPolicy.FullTextIndexes.Count != 0
                || actual.VectorEmbeddingPolicy != null
                || actual.FullTextPolicy != null)
            {
                throw new InvalidOperationException(
                    $"Cosmos container '{expected.Id}' has unexpected indexing configuration. " +
                    $"Actual included=[{string.Join(",", GetPaths(actual.IndexingPolicy.IncludedPaths))}], " +
                    $"excluded=[{string.Join(",", GetPaths(actual.IndexingPolicy.ExcludedPaths))}]; " +
                    $"expected included=[{string.Join(",", GetPaths(expected.IndexingPolicy.IncludedPaths))}], " +
                    $"excluded=[{string.Join(",", GetPaths(expected.IndexingPolicy.ExcludedPaths))}].");
            }
        }

        private static async Task ValidateAsync(
            Database database,
            CancellationToken cancellationToken)
        {
            var throughputResponse = await database.ReadThroughputAsync(
                new RequestOptions(),
                cancellationToken);
            ValidateDatabaseThroughput(throughputResponse.Resource);

            foreach (var expected in GetContainerProperties())
            {
                var container = database.GetContainer(expected.Id);
                ContainerProperties actual;
                try
                {
                    actual = (await container.ReadContainerAsync(
                        cancellationToken: cancellationToken)).Resource;
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        "A required Cosmos container must be provisioned before production startup.");
                }

                ValidateContainerProperties(actual, expected);
                var dedicatedThroughput = await container.ReadThroughputAsync(cancellationToken);
                ValidateContainerThroughput(expected.Id, dedicatedThroughput);
            }
        }

        internal static void ValidateContainerThroughput(string containerName, int? throughput)
        {
            if (throughput.HasValue)
            {
                throw new InvalidOperationException(
                    $"Cosmos container '{containerName}' must inherit shared database throughput.");
            }
        }

        private static ContainerProperties CreateListsProperties()
        {
            return CreateContainerProperties(
                CosmosContainerNames.Lists,
                defaultTimeToLive: -1,
                includedPaths: new[] { "/*" },
                excludedPaths: new[] { "/token/?", "/\"_etag\"/?" });
        }

        private static ContainerProperties CreateChannelsProperties()
        {
            return CreateContainerProperties(
                CosmosContainerNames.Channels,
                defaultTimeToLive: null,
                includedPaths: new[] { "/*" },
                excludedPaths: new[] { "/videos/*", "/\"_etag\"/?" });
        }

        private static ContainerProperties CreateShareLinksProperties()
        {
            return CreateContainerProperties(
                CosmosContainerNames.ShareLinks,
                defaultTimeToLive: -1,
                includedPaths: new[]
                {
                    "/listId/?",
                    "/createdAt/?",
                    "/expiresAfter/?",
                    "/usedAt/?"
                },
                excludedPaths: new[] { "/*", "/\"_etag\"/?" });
        }

        private static ContainerProperties CreateContainerProperties(
            string id,
            int? defaultTimeToLive,
            IEnumerable<string> includedPaths,
            IEnumerable<string> excludedPaths)
        {
            var properties = new ContainerProperties(id, IdPartitionKeyPath)
            {
                DefaultTimeToLive = defaultTimeToLive,
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent
                }
            };
            foreach (var path in includedPaths)
            {
                properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
            }

            foreach (var path in excludedPaths)
            {
                properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = path });
            }

            return properties;
        }

        private static IEnumerable<string> GetPaths<T>(IEnumerable<T> paths)
        {
            return paths.Select(path => path switch
                {
                    IncludedPath included => included.Path,
                    ExcludedPath excluded => excluded.Path,
                    _ => string.Empty
                })
                .OrderBy(path => path, StringComparer.Ordinal);
        }
    }
}
