using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosContainerInitializer
    {
        private const string IdPartitionKeyPath = "/id";

        public async Task InitializeAsync(Database database, CosmosOptions options, CancellationToken cancellationToken = default)
        {
            foreach (var properties in GetContainerProperties(options))
            {
                await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken);
            }
        }

        public static IReadOnlyList<ContainerProperties> GetContainerProperties(CosmosOptions options)
        {
            return new[]
            {
                CreateListsProperties(options.ListsContainer),
                CreateChannelsProperties(options.ChannelsContainer),
                CreateShareLinksProperties(options.ShareLinksContainer),
                CreateSystemProperties(options.SystemContainer)
            };
        }

        private static ContainerProperties CreateListsProperties(string id)
        {
            return CreateTtlContainer(id, new[] { "/channels/*" });
        }

        private static ContainerProperties CreateChannelsProperties(string id)
        {
            var properties = CreateTtlContainer(
                id,
                new[] { "/videos/*" },
                new[] { "/staleAfter/?", "/subscriptionCount/?", "/status/?" });
            properties.IndexingPolicy.CompositeIndexes.Add(new System.Collections.ObjectModel.Collection<CompositePath>
            {
                new CompositePath { Path = "/staleAfter", Order = CompositePathSortOrder.Ascending },
                new CompositePath { Path = "/id", Order = CompositePathSortOrder.Ascending }
            });
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
