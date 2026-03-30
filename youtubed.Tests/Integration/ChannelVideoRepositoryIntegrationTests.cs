using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelVideoRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly ChannelVideoRepository _repository;

        public ChannelVideoRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _repository = new ChannelVideoRepository(fixture.ConnectionFactory);
        }

        [LocalDbFact]
        public async Task RefreshAsync_WithNoVideosStillUpdatesStaleAfter()
        {
            var beforeRefresh = DateTimeOffset.UtcNow;

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, VisibleAfter)
                VALUES (N'channel-1', N'https://www.youtube.com/channel/channel-1', N'Channel', N'thumb.png', N'playlist-1', @staleAfter, @visibleAfter);
                ",
                new
                {
                    staleAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
                    visibleAfter = DateTimeOffset.UtcNow.AddMinutes(-5)
                });

            await _repository.RefreshAsync(
                "channel-1",
                DateTimeOffset.UtcNow.Subtract(Constants.VideoMaxAge),
                Array.Empty<ChannelVideoRecord>(),
                DateTimeOffset.UtcNow.AddHours(1));

            var staleAfter = await ScalarAsync<DateTimeOffset>(
                "SELECT StaleAfter FROM Channel WHERE Id = N'channel-1';");

            Assert.True(staleAfter > beforeRefresh);
        }
    }
}
