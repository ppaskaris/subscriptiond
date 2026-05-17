using System;
using System.Threading.Tasks;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.Services
{
    public class ChannelUrlLookupCacheTests
    {
        [Fact]
        public void TryGetChannelId_ReturnsCachedChannelIdBeforeExpiration()
        {
            using var cache = new ChannelUrlLookupCache(TimeSpan.FromHours(1), 1000);

            cache.Set("https://www.youtube.com/channel/channel-1", "channel-1");

            var found = cache.TryGetChannelId("https://www.youtube.com/channel/channel-1", out var channelId);

            Assert.True(found);
            Assert.Equal("channel-1", channelId);
        }

        [Fact]
        public async Task TryGetChannelId_ReturnsFalseAfterExpiration()
        {
            using var cache = new ChannelUrlLookupCache(TimeSpan.FromMilliseconds(1), 1000);

            cache.Set("https://www.youtube.com/channel/channel-1", "channel-1");
            await Task.Delay(20);

            var found = cache.TryGetChannelId("https://www.youtube.com/channel/channel-1", out var channelId);

            Assert.False(found);
            Assert.Null(channelId);
        }

        [Fact]
        public void Set_DoesNotCacheMoreEntriesThanSizeLimit()
        {
            using var cache = new ChannelUrlLookupCache(TimeSpan.FromHours(1), 2);

            cache.Set("url-1", "channel-1");
            cache.Set("url-2", "channel-2");
            cache.Set("url-3", "channel-3");

            var cachedCount = 0;
            cachedCount += cache.TryGetChannelId("url-1", out _) ? 1 : 0;
            cachedCount += cache.TryGetChannelId("url-2", out _) ? 1 : 0;
            cachedCount += cache.TryGetChannelId("url-3", out _) ? 1 : 0;

            Assert.Equal(2, cachedCount);
        }

        [Fact]
        public void Set_CachesNullChannelId()
        {
            using var cache = new ChannelUrlLookupCache(TimeSpan.FromHours(1), 1000);

            cache.Set("missing-url", null);

            var found = cache.TryGetChannelId("missing-url", out var channelId);

            Assert.True(found);
            Assert.Null(channelId);
        }
    }
}
