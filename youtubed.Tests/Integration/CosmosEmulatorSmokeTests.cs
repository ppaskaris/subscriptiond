using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosEmulatorSmokeTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosEmulatorSmokeTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task CanCreateContainersAndWriteSystemDocument()
        {
            var container = _fixture.GetContainer(CosmosTestFixture.SystemContainerName);
            var document = new SystemDocument
            {
                Id = "smoke-test",
                DocumentType = "system"
            };

            await container.CreateItemAsync(document, new PartitionKey(document.Id));

            var response = await container.ReadItemAsync<SystemDocument>(
                document.Id,
                new PartitionKey(document.Id));

            Assert.Equal(document.Id, response.Resource.Id);
            Assert.Equal(document.DocumentType, response.Resource.DocumentType);
        }

        private sealed class SystemDocument
        {
            public string Id { get; set; }

            public string DocumentType { get; set; }
        }
    }
}
