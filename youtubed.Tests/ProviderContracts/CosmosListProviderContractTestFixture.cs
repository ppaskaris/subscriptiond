using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class CosmosListProviderContractTestFixture : IProviderContractTestFixture
    {
        private readonly CosmosTestFixture _fixture;
        private readonly bool _projectRefreshResults;

        public CosmosListProviderContractTestFixture(
            CosmosTestFixture fixture,
            bool projectRefreshResults = true)
        {
            _fixture = fixture;
            _projectRefreshResults = projectRefreshResults;
        }

        public string ProviderName => "Cosmos";

        public ExpirationPurgeBehavior PurgeBehavior => ExpirationPurgeBehavior.NoOp;

        public async Task ResetAsync()
        {
            await DeleteAllAsync(
                _fixture.GetContainer(CosmosTestFixture.ListsContainerName));
            await DeleteAllAsync(
                _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName));
            await DeleteAllAsync(
                _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName));
            await DeleteAllAsync(
                _fixture.GetContainer(CosmosTestFixture.SystemContainerName));
        }

        public ProviderContractTestContext CreateContext(IAppClock clock)
        {
            var lists = _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
            var channels = _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName);
            var shareLinks = _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName);
            var system = _fixture.GetContainer(CosmosTestFixture.SystemContainerName);

            return new ProviderContractTestContext(
                new CosmosListRepository(lists, channels, clock),
                new SeededCosmosChannelRepository(
                    channels,
                    lists,
                    clock,
                    _projectRefreshResults),
                new CosmosShareLinkRepository(shareLinks, lists, clock),
                new CosmosListProjectionRepository(lists, channels, clock),
                new CosmosWorkerStateStore(system, clock),
                new CosmosExpirationPurger());
        }

        private static async Task DeleteAllAsync(Container container)
        {
            using var iterator = container.GetItemQueryIterator<string>(
                "SELECT VALUE c.id FROM c");
            while (iterator.HasMoreResults)
            {
                foreach (var id in await iterator.ReadNextAsync())
                {
                    await container.DeleteItemAsync<object>(id, new PartitionKey(id));
                }
            }
        }

        private sealed class SeededCosmosChannelRepository : IChannelRepository
        {
            private readonly Container _channels;
            private readonly Container _lists;
            private readonly IAppClock _clock;
            private readonly bool _projectRefreshResults;

            public SeededCosmosChannelRepository(
                Container channels,
                Container lists,
                IAppClock clock,
                bool projectRefreshResults)
            {
                _channels = channels;
                _lists = lists;
                _clock = clock;
                _projectRefreshResults = projectRefreshResults;
            }

            public async Task<Channel> GetByIdAsync(string id)
            {
                try
                {
                    var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                        id,
                        new PartitionKey(id));
                    return CosmosDocumentMapper.ToChannel(response.Resource);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
            }

            public async Task SaveDiscoveredChannelAsync(
                Channel channel,
                DateTimeOffset staleAfter)
            {
                var document = CosmosDocumentMapper.ToChannelDocument(
                    channel,
                    _clock.UtcNow,
                    TimeSpan.FromDays(7));
                await _channels.CreateItemAsync(document, new PartitionKey(document.Id));
            }

            public async Task SaveRefreshResultsAsync(
                IReadOnlyCollection<ChannelRefreshResult> results,
                CancellationToken cancellationToken)
            {
                foreach (var result in results)
                {
                    var channelDocument = CosmosDocumentMapper.ToChannelDocument(
                        result.Channel,
                        _clock.UtcNow,
                        TimeSpan.FromDays(7));
                    await _channels.UpsertItemAsync(
                        channelDocument,
                        new PartitionKey(channelDocument.Id),
                        cancellationToken: cancellationToken);

                    if (_projectRefreshResults)
                    {
                        await ProjectRefreshResultAsync(result.Channel, cancellationToken);
                    }
                }
            }

            private async Task ProjectRefreshResultAsync(
                Channel channel,
                CancellationToken cancellationToken)
            {
                using var iterator = _lists.GetItemQueryIterator<CosmosListDocument>();
                while (iterator.HasMoreResults)
                {
                    foreach (var list in await iterator.ReadNextAsync(cancellationToken))
                    {
                        if (!list.Channels.Any(projected => projected.Id == channel.Id))
                        {
                            continue;
                        }

                        list.Channels = list.Channels
                            .Select(projected => projected.Id == channel.Id
                                ? CosmosDocumentMapper.ToProjectedChannelDocument(channel)
                                : projected)
                            .ToArray();
                        await _lists.ReplaceItemAsync(
                            list,
                            list.Id,
                            new PartitionKey(list.Id),
                            new ItemRequestOptions { IfMatchEtag = list.ETag },
                            cancellationToken);
                    }
                }
            }

            public Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
                DateTimeOffset now,
                int take,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public async Task<IReadOnlyList<Channel>> GetBatchAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken)
            {
                var lists = new List<CosmosListDocument>();
                using (var iterator = _lists.GetItemQueryIterator<CosmosListDocument>())
                {
                    while (iterator.HasMoreResults)
                    {
                        lists.AddRange(await iterator.ReadNextAsync(cancellationToken));
                    }
                }

                var channels = new List<Channel>();
                foreach (var channelId in channelIds.Distinct(StringComparer.Ordinal))
                {
                    try
                    {
                        var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                            channelId,
                            new PartitionKey(channelId),
                            cancellationToken: cancellationToken);
                        var channel = CosmosDocumentMapper.ToChannel(response.Resource);
                        channel.SubscribedListIds = lists
                            .Where(list => list.Channels.Any(projected => projected.Id == channelId))
                            .Select(list => Guid.Parse(list.Id))
                            .ToArray();
                        channel.SubscriptionCount = channel.SubscribedListIds.Count;
                        channels.Add(channel);
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.NotFound)
                    {
                    }
                }

                return channels;
            }

            public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now) =>
                throw new NotSupportedException();
        }
    }
}
