using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    public sealed class CosmosProviderContractTestFixture : IProviderContractTestFixture
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosProviderContractTestFixture(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        public string ProviderName => "Cosmos";

        public ExpirationPurgeBehavior PurgeBehavior => ExpirationPurgeBehavior.NoOp;

        public async Task ResetAsync()
        {
            await DeleteAllAsync<CosmosListDocument>(_fixture.Context.Lists);
            await DeleteAllAsync<CosmosChannelDocument>(_fixture.Context.Channels);
            await DeleteAllAsync<CosmosShareLinkDocument>(_fixture.Context.ShareLinks);
        }

        public ProviderContractTestContext CreateContext(IAppClock clock)
        {
            var lists = new CosmosListRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosListRepository>.Instance);
            var channels = new CosmosChannelRepository(
                _fixture.Context,
                NullLogger<CosmosChannelRepository>.Instance);
            var shareLinks = new CosmosShareLinkRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosShareLinkRepository>.Instance);
            return new ProviderContractTestContext(
                lists,
                channels,
                shareLinks,
                new CosmosExpirationPurger());
        }

        private static async Task DeleteAllAsync<T>(Container container)
        {
            using var iterator = container.GetItemQueryIterator<string>(
                new QueryDefinition("SELECT VALUE c.id FROM c"));
            while (iterator.HasMoreResults)
            {
                foreach (var id in await iterator.ReadNextAsync())
                {
                    try
                    {
                        await container.DeleteItemAsync<T>(id, new PartitionKey(id));
                    }
                    catch (CosmosException exception) when (
                        exception.StatusCode == HttpStatusCode.NotFound)
                    {
                    }
                }
            }
        }

    }
}
