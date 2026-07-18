using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class CosmosListProviderContractTestFixture : IProviderContractTestFixture
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosListProviderContractTestFixture(CosmosTestFixture fixture)
        {
            _fixture = fixture;
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
        }

        public ProviderContractTestContext CreateContext(IAppClock clock)
        {
            var lists = _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
            var channels = _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName);
            var shareLinks = _fixture.GetContainer(CosmosTestFixture.ShareLinksContainerName);

            return new ProviderContractTestContext(
                new CosmosListRepository(lists, channels, clock),
                new SeededCosmosChannelRepository(channels, lists, clock),
                new CosmosShareLinkRepository(shareLinks, lists, clock),
                null,
                null,
                null);
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

            public SeededCosmosChannelRepository(
                Container channels,
                Container lists,
                IAppClock clock)
            {
                _channels = channels;
                _lists = lists;
                _clock = clock;
            }

            public async Task<ChannelModel> GetByIdAsync(string id)
            {
                try
                {
                    var response = await _channels.ReadItemAsync<CosmosChannelDocument>(
                        id,
                        new PartitionKey(id));
                    var channel = CosmosDocumentMapper.ToChannel(response.Resource);
                    return new ChannelModel
                    {
                        Id = channel.Id,
                        Url = channel.Url,
                        Title = channel.Title,
                        Thumbnail = channel.Thumbnail,
                        PlaylistId = channel.PlaylistId,
                        StaleAfter = channel.StaleAfter,
                        Status = channel.Status,
                        StatusReason = channel.StatusReason,
                        StatusUpdatedAt = channel.StatusUpdatedAt
                    };
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
            }

            public async Task SaveDiscoveredChannelAsync(
                ChannelModel channel,
                DateTimeOffset staleAfter)
            {
                var document = CosmosDocumentMapper.ToChannelDocument(
                    new Channel
                    {
                        Id = channel.Id,
                        Url = channel.Url,
                        Title = channel.Title,
                        Thumbnail = channel.Thumbnail,
                        PlaylistId = channel.PlaylistId,
                        StaleAfter = staleAfter,
                        Status = channel.Status,
                        StatusReason = channel.StatusReason,
                        StatusUpdatedAt = channel.StatusUpdatedAt
                    },
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

                    using var iterator = _lists.GetItemQueryIterator<CosmosListDocument>();
                    while (iterator.HasMoreResults)
                    {
                        foreach (var list in await iterator.ReadNextAsync(cancellationToken))
                        {
                            if (!list.Channels.Any(channel => channel.Id == result.Channel.Id))
                            {
                                continue;
                            }

                            list.Channels = list.Channels
                                .Select(channel => channel.Id == result.Channel.Id
                                    ? CosmosDocumentMapper.ToProjectedChannelDocument(result.Channel)
                                    : channel)
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
            }

            public Task UpdateMetadataAsync(
                string id,
                string url,
                string title,
                string thumbnail,
                string playlistId) => throw new NotSupportedException();

            public Task MarkUnavailableAsync(
                string id,
                ChannelStatusReason reason,
                DateTimeOffset statusUpdatedAt,
                DateTimeOffset staleAfter) => throw new NotSupportedException();

            public Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
                DateTimeOffset now,
                int take,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<IReadOnlyList<Channel>> GetBatchAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken) => throw new NotSupportedException();

            public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now) =>
                throw new NotSupportedException();
        }
    }
}
