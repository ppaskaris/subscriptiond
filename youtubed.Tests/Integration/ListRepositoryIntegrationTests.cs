using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Models;
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

        [LocalDbFact]
        public async Task CreateGetAndUpdateAsync_PersistPlaybackRate()
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = new byte[40],
                Title = "Playback List",
                PlaybackRate = 1.50m,
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1)
            };

            await _repository.CreateAsync(list);
            var created = await _repository.GetAsync(list.Id);
            await _repository.UpdateAsync(list.Id, "Updated Playback List", 2.00m);
            var updated = await _repository.GetAsync(list.Id);

            Assert.Equal(1.50m, created.PlaybackRate);
            Assert.Equal("Updated Playback List", updated.Title);
            Assert.Equal(2.00m, updated.PlaybackRate);
        }
    }
}
