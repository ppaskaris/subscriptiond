using System;
using System.Threading.Tasks;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Services
{
    public class ChannelService : IChannelService
    {
        private readonly IChannelRepository _channelRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;

        public ChannelService(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService,
            IAppClock clock)
        {
            _channelRepository = channelRepository;
            _youtubeService = youtubeService;
            _clock = clock;
        }

        public async Task<ChannelModel> GetOrCreateChannelAsync(string url)
        {
            YoutubeChannel channel;

            // Vanity URLs cannot be mapped to Channel ID using the API. To
            // work around this, support adding channels by video URL instead.
            if (Constants.YoutubeVideoExpression.IsMatch(url))
            {
                channel = await _youtubeService.GetVideoChannelAsync(url);
                if (channel == null)
                {
                    return null;
                }

                url = string.Format(Constants.YoutubeChannelUrl, channel.Id);
            }
            else
            {
                var cached = await _channelRepository.GetByUrlAsync(url);
                if (cached != null)
                {
                    return cached;
                }

                channel = await _youtubeService.GetChannelAsync(url);
                if (channel == null)
                {
                    return null;
                }
            }

            var model = new ChannelModel
            {
                Id = channel.Id,
                Url = url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId
            };

            await _channelRepository.SaveDiscoveredChannelAsync(model, DateTimeOffset.MinValue);
            return model;
        }

        public Task<StaleChannelModel> GetNextStaleChannelOrDefaultAsync()
        {
            var now = _clock.UtcNow;
            var visibilityTimeout = _clock.RandomDelay(
                Constants.VisibilityTimeoutMin,
                Constants.VisibilityTimeoutMax);

            // The database lease is the only worker-coordination mechanism.
            // If a refresh fails, the channel becomes eligible again once this
            // visibility window expires and another worker can claim it.
            return _channelRepository.ClaimNextStaleChannelAsync(now, now.Add(visibilityTimeout));
        }

        public async Task RefreshMetadataAsync(StaleChannelModel channel)
        {
            var refreshed = await _youtubeService.GetChannelAsync(channel.Url);
            if (refreshed == null)
            {
                return;
            }

            if (refreshed.Title == channel.Title && refreshed.Thumbnail == channel.Thumbnail)
            {
                return;
            }

            await _channelRepository.UpdateMetadataAsync(
                channel.Id,
                refreshed.Title,
                refreshed.Thumbnail);
        }

        public Task<int> RemoveOrphanChannelsAsync()
        {
            return _channelRepository.RemoveOrphanChannelsAsync(_clock.UtcNow);
        }
    }
}
