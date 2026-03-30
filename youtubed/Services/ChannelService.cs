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

        public ChannelService(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService)
        {
            _channelRepository = channelRepository;
            _youtubeService = youtubeService;
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
            var now = DateTimeOffset.Now;
            var visibilityTimeout = Constants.RandomlyBetween(
                Constants.VisibilityTimeoutMin,
                Constants.VisibilityTimeoutMax);
            return _channelRepository.ClaimNextStaleChannelAsync(now, now.Add(visibilityTimeout));
        }

        public Task<int> RemoveOrphanChannelsAsync()
        {
            return _channelRepository.RemoveOrphanChannelsAsync(DateTimeOffset.Now);
        }
    }
}
