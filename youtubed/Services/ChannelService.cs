using System;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Services
{
    public class ChannelService : IChannelService
    {
        private readonly IChannelRepository _channelRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;
        private readonly IChannelUrlLookupCache _channelUrlLookupCache;

        public ChannelService(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService,
            IAppClock clock,
            IChannelUrlLookupCache channelUrlLookupCache)
        {
            _channelRepository = channelRepository;
            _youtubeService = youtubeService;
            _clock = clock;
            _channelUrlLookupCache = channelUrlLookupCache;
        }

        public async Task<ChannelModel> GetOrCreateChannelAsync(string url)
        {
            if (_channelUrlLookupCache.TryGetChannelId(url, out var cachedChannelId))
            {
                if (cachedChannelId == null)
                {
                    return null;
                }

                var cached = await _channelRepository.GetByIdAsync(cachedChannelId);
                if (cached != null)
                {
                    return cached;
                }

                var cachedChannel = await _youtubeService.GetChannelByIdAsync(cachedChannelId);
                if (cachedChannel != null)
                {
                    return await SaveDiscoveredChannelAsync(cachedChannel);
                }

                _channelUrlLookupCache.Set(url, null);
                return null;
            }

            var channel = await ResolveSubmittedUrlAsync(url);
            _channelUrlLookupCache.Set(url, channel?.Id);
            if (channel == null)
            {
                return null;
            }

            return await SaveDiscoveredChannelAsync(channel);
        }

        private async Task<YoutubeChannel> ResolveSubmittedUrlAsync(string url)
        {
            // Vanity URLs cannot be mapped to Channel ID using the API. To
            // work around this, support adding channels by video URL instead.
            if (Constants.YoutubeVideoExpression.IsMatch(url))
            {
                return await _youtubeService.GetVideoChannelAsync(url);
            }

            return await _youtubeService.GetChannelByUrlAsync(url);
        }

        private async Task<ChannelModel> SaveDiscoveredChannelAsync(YoutubeChannel channel)
        {
            var model = new ChannelModel
            {
                Id = channel.Id,
                Url = string.Format(Constants.YoutubeChannelUrl, channel.Id),
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId
            };

            await _channelRepository.SaveDiscoveredChannelAsync(model, DateTimeOffset.MinValue);
            return model;
        }
    }
}
