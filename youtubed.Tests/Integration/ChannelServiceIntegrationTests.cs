using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class ChannelServiceIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeYoutubeService _youtubeService;
        private readonly ChannelService _service;

        public ChannelServiceIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _youtubeService = new FakeYoutubeService();
            _service = new ChannelService(
                new ChannelRepository(fixture.ConnectionFactory),
                _youtubeService,
                new FakeAppClock(),
                new ChannelUrlLookupCache());
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_CachesChannelByUrl()
        {
            const string url = "https://www.youtube.com/channel/channel-1";
            _youtubeService.SetChannel(url, new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Integration Channel",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-1"
            });

            var first = await _service.GetOrCreateChannelAsync(url);
            var second = await _service.GetOrCreateChannelAsync(url);
            var count = await ScalarAsync<int>("SELECT COUNT(*) FROM Channel WHERE Url = @url;", new { url });

            Assert.Equal("channel-1", first.Id);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, _youtubeService.GetChannelCallCount);
            Assert.Equal(1, count);
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_VideoUrlFallbackStoresCanonicalChannelUrl()
        {
            const string videoUrl = "https://www.youtube.com/watch?v=video-1";
            _youtubeService.SetVideoChannel(videoUrl, new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Video Channel",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-2"
            });

            var channel = await _service.GetOrCreateChannelAsync(videoUrl);
            var persistedUrl = await ScalarAsync<string>("SELECT Url FROM Channel WHERE Id = N'channel-2';");

            Assert.Equal("channel-2", channel.Id);
            Assert.Equal("https://www.youtube.com/channel/channel-2", persistedUrl);
            Assert.Equal(1, _youtubeService.GetVideoChannelCallCount);
            Assert.Equal(0, _youtubeService.GetChannelCallCount);
        }

        [LocalDbFact]
        public async Task GetOrCreateChannelAsync_VideoFallbackMarksExistingChannelStaleAgain()
        {
            const string videoUrl = "https://www.youtube.com/watch?v=video-2";
            const string canonicalUrl = "https://www.youtube.com/channel/channel-3";
            var futureStaleAfter = DateTimeOffset.UtcNow.AddHours(2);

            await ExecuteAsync(
                @"
                INSERT INTO Channel (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter)
                VALUES (N'channel-3', @url, N'Existing', N'thumb.png', N'playlist-3', @staleAfter);
                ",
                new
                {
                    url = canonicalUrl,
                    staleAfter = futureStaleAfter
                });

            _youtubeService.SetVideoChannel(videoUrl, new YoutubeChannel
            {
                Id = "channel-3",
                Title = "Existing",
                Thumbnail = "thumb.png",
                PlaylistId = "playlist-3"
            });

            await _service.GetOrCreateChannelAsync(videoUrl);

            var staleAfter = await ScalarAsync<DateTimeOffset>("SELECT StaleAfter FROM Channel WHERE Id = N'channel-3';");

            Assert.True(staleAfter <= DateTimeOffset.UtcNow.AddMinutes(-1));
        }
    }
}
