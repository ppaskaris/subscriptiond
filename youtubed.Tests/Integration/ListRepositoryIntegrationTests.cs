using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ListRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ListRepository _repository;

        public ListRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ListRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task GetViewAsync_ReturnsNullForMissingList()
        {
            var view = await _repository.GetViewAsync(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddDays(1),
                DateTimeOffset.UtcNow);

            Assert.Null(view);
        }
    }
}
