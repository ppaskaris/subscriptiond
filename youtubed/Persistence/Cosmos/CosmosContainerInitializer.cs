using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializer
    {
        private const string IdPartitionKeyPath = "/id";
        private const string RecoveryPartitionKeyPath = "/listId";

        public async Task InitializeAsync(Database database, CosmosOptions options, CancellationToken cancellationToken = default)
        {
            using var operationScope = CosmosLogicalOperationScope.Begin(
                CosmosLogicalOperationScope.ContainerInitialization);
            foreach (var properties in GetContainerProperties(options))
            {
                var response = await database.CreateContainerIfNotExistsAsync(
                    properties,
                    cancellationToken: cancellationToken);
                var actual = (await response.Container.ReadContainerAsync(
                    cancellationToken: cancellationToken)).Resource;
                if (!string.Equals(
                    actual.PartitionKeyPath,
                    properties.PartitionKeyPath,
                    System.StringComparison.Ordinal))
                {
                    throw new System.InvalidOperationException(
                        $"Cosmos container '{properties.Id}' has partition key " +
                        $"'{actual.PartitionKeyPath}', expected '{properties.PartitionKeyPath}'.");
                }

                if (HasPolicyDrift(actual, properties))
                {
                    await response.Container.ReplaceContainerAsync(
                        properties,
                        cancellationToken: cancellationToken);
                }
            }
        }

        public static IReadOnlyList<ContainerProperties> GetContainerProperties(CosmosOptions options)
        {
            return new[]
            {
                CreateListsProperties(options.ListsContainer),
                CreateChannelsProperties(options.ChannelsContainer),
                CreateShareLinksProperties(options.ShareLinksContainer),
                CreateSystemProperties(options.SystemContainer),
                CreateRecoveryProperties(options.RecoveryContainer)
            };
        }

        private static ContainerProperties CreateListsProperties(string id)
        {
            var properties = CreateTtlContainer(id, new[] { "/channels/*" });
            AddAscendingComposite(
                properties,
                "/membershipRecoveryPending",
                "/membershipRecoveryDueAt",
                "/id");
            AddAscendingComposite(
                properties,
                "/membershipRecoveryDueAt",
                "/id");
            return properties;
        }

        private static ContainerProperties CreateChannelsProperties(string id)
        {
            var properties = CreateTtlContainer(
                id,
                new[] { "/videos/*" },
                new[]
                {
                    "/staleAfter/?",
                    "/subscriptionCount/?",
                    "/status/?",
                    "/projectionRecoveryPending/?",
                    "/projectionVersion/?",
                    "/subscriptionGeneration/?",
                    "/projectionRecoveryDueAt/?"
                });
            AddAscendingComposite(properties, "/staleAfter", "/id");
            AddAscendingComposite(
                properties,
                "/projectionRecoveryPending",
                "/projectionRecoveryDueAt",
                "/id");
            AddAscendingComposite(
                properties,
                "/projectionRecoveryDueAt",
                "/id");
            return properties;
        }

        private static ContainerProperties CreateShareLinksProperties(string id)
        {
            return CreateTtlContainer(
                id,
                includedPaths: new[] { "/listId/?", "/createdAt/?", "/expiresAfter/?", "/usedAt/?" });
        }

        private static ContainerProperties CreateSystemProperties(string id)
        {
            return new ContainerProperties(id, IdPartitionKeyPath)
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    ExcludedPaths = { new ExcludedPath { Path = "/*" } }
                }
            };
        }

        private static ContainerProperties CreateRecoveryProperties(string id)
        {
            var properties = new ContainerProperties(id, RecoveryPartitionKeyPath)
            {
                IndexingPolicy = new IndexingPolicy
                {
                    Automatic = true,
                    IndexingMode = IndexingMode.Consistent,
                    ExcludedPaths = { new ExcludedPath { Path = "/*" } }
                }
            };
            foreach (var path in new[]
            {
                "/kind/?",
                "/state/?",
                "/nextAttemptAt/?",
                "/nextCheckAt/?",
                "/leaseUntil/?",
                "/active/?",
                "/channelId/?",
                "/listId/?"
            })
            {
                properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
            }

            AddAscendingComposite(
                properties,
                "/kind",
                "/active",
                "/nextAttemptAt",
                "/listId",
                "/id");
            AddAscendingComposite(
                properties,
                "/nextAttemptAt",
                "/listId",
                "/id");
            AddAscendingComposite(
                properties,
                "/kind",
                "/nextCheckAt",
                "/listId",
                "/id");
            AddAscendingComposite(
                properties,
                "/nextCheckAt",
                "/listId",
                "/id");
            AddAscendingComposite(
                properties,
                "/kind",
                "/active",
                "/channelId",
                "/id");
            AddAscendingComposite(
                properties,
                "/channelId",
                "/id");
            return properties;
        }

        private static void AddAscendingComposite(
            ContainerProperties properties,
            params string[] paths)
        {
            var composite = new System.Collections.ObjectModel.Collection<CompositePath>();
            foreach (var path in paths)
            {
                composite.Add(new CompositePath
                {
                    Path = path,
                    Order = CompositePathSortOrder.Ascending
                });
            }

            properties.IndexingPolicy.CompositeIndexes.Add(composite);
        }

        private static bool HasPolicyDrift(
            ContainerProperties actual,
            ContainerProperties expected)
        {
            return actual.DefaultTimeToLive != expected.DefaultTimeToLive
                || actual.IndexingPolicy.Automatic != expected.IndexingPolicy.Automatic
                || actual.IndexingPolicy.IndexingMode != expected.IndexingPolicy.IndexingMode
                || !GetPaths(actual.IndexingPolicy.IncludedPaths)
                    .SequenceEqual(GetPaths(expected.IndexingPolicy.IncludedPaths))
                || !GetPaths(actual.IndexingPolicy.ExcludedPaths)
                    .SequenceEqual(GetPaths(expected.IndexingPolicy.ExcludedPaths))
                || !GetComposites(actual.IndexingPolicy.CompositeIndexes)
                    .SequenceEqual(GetComposites(expected.IndexingPolicy.CompositeIndexes));
        }

        private static IEnumerable<string> GetPaths<T>(
            IEnumerable<T> paths)
        {
            return paths
                .Select(path => path switch
                {
                    IncludedPath included => included.Path,
                    ExcludedPath excluded => excluded.Path,
                    _ => string.Empty
                })
                .OrderBy(path => path, System.StringComparer.Ordinal);
        }

        private static IEnumerable<string> GetComposites(
            IEnumerable<System.Collections.ObjectModel.Collection<CompositePath>> composites)
        {
            return composites
                .Select(composite => string.Join(
                    "|",
                    composite.Select(path => $"{path.Path}:{path.Order}")))
                .OrderBy(value => value, System.StringComparer.Ordinal);
        }

        private static ContainerProperties CreateTtlContainer(
            string id,
            IReadOnlyCollection<string> excludedPaths = null,
            IReadOnlyCollection<string> includedPaths = null)
        {
            var policy = new IndexingPolicy
            {
                Automatic = true,
                IndexingMode = IndexingMode.Consistent
            };

            if (includedPaths != null)
            {
                foreach (var path in includedPaths)
                {
                    policy.IncludedPaths.Add(new IncludedPath { Path = path });
                }

                policy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
            }
            else
            {
                policy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
            }

            if (excludedPaths != null)
            {
                foreach (var path in excludedPaths)
                {
                    policy.ExcludedPaths.Add(new ExcludedPath { Path = path });
                }
            }

            return new ContainerProperties(id, IdPartitionKeyPath)
            {
                DefaultTimeToLive = -1,
                IndexingPolicy = policy
            };
        }
    }
}
