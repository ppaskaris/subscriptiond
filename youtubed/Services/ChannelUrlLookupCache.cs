using System;
using Microsoft.Extensions.Caching.Memory;

namespace youtubed.Services
{
    public sealed class ChannelUrlLookupCache : IChannelUrlLookupCache, IDisposable
    {
        private static readonly object NullChannelId = new object();

        private readonly IMemoryCache _cache;
        private readonly IDisposable _disposableCache;
        private readonly TimeSpan _duration;

        public ChannelUrlLookupCache()
            : this(Constants.ChannelLookupCacheDuration, Constants.ChannelLookupCacheSizeLimit)
        {
        }

        public ChannelUrlLookupCache(TimeSpan duration, int sizeLimit)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            if (sizeLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeLimit));
            }

            _duration = duration;
            var cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = sizeLimit
            });
            _cache = cache;
            _disposableCache = cache;
        }

        public bool TryGetChannelId(string url, out string channelId)
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            if (!_cache.TryGetValue(url, out object cached))
            {
                channelId = null;
                return false;
            }

            channelId = ReferenceEquals(cached, NullChannelId)
                ? null
                : (string)cached;
            return true;
        }

        public void Set(string url, string channelId)
        {
            if (url == null)
            {
                throw new ArgumentNullException(nameof(url));
            }

            _cache.Set(
                url,
                channelId ?? NullChannelId,
                new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_duration)
                    .SetSize(1));
        }

        public void Dispose()
        {
            _disposableCache.Dispose();
        }
    }
}
