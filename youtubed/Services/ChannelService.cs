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

                channel = await _youtubeService.GetChannelByUrlAsync(url);
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

        public async Task<StaleChannelModel> RefreshMetadataAsync(StaleChannelModel channel)
        {
            var refreshed = await _youtubeService.GetChannelByIdAsync(channel.Id);
            if (refreshed == null)
            {
                var now = _clock.UtcNow;
                await _channelRepository.MarkUnavailableAsync(
                    channel.Id,
                    ChannelStatusReason.NotFound,
                    now,
                    now.Add(Constants.ChannelUnavailableStaleDelay));
                return null;
            }

            var refreshedUrl = string.Format(Constants.YoutubeChannelUrl, refreshed.Id);
            if (refreshedUrl == channel.Url &&
                refreshed.Title == channel.Title &&
                refreshed.Thumbnail == channel.Thumbnail &&
                refreshed.PlaylistId == channel.PlaylistId &&
                channel.Status == ChannelStatus.Active &&
                channel.StatusReason == ChannelStatusReason.None &&
                channel.StatusUpdatedAt == null)
            {
                return channel;
            }

            await _channelRepository.UpdateMetadataAsync(
                channel.Id,
                refreshedUrl,
                refreshed.Title,
                refreshed.Thumbnail,
                refreshed.PlaylistId);

            return new StaleChannelModel
            {
                Id = channel.Id,
                Url = refreshedUrl,
                Title = refreshed.Title,
                Thumbnail = refreshed.Thumbnail,
                PlaylistId = refreshed.PlaylistId,
                Status = ChannelStatus.Active,
                StatusReason = ChannelStatusReason.None,
                StatusUpdatedAt = null
            };
        }

        public Task<int> RemoveOrphanChannelsAsync()
        {
            return _channelRepository.RemoveOrphanChannelsAsync(_clock.UtcNow);
        }
    }
}
