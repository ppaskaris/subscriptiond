using System;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Services
{
    public class ChannelVideoService : IChannelVideoService
    {
        private readonly IChannelVideoRepository _channelVideoRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;

        public ChannelVideoService(
            IChannelVideoRepository channelVideoRepository,
            IYoutubeService youtubeService,
            IAppClock clock)
        {
            _channelVideoRepository = channelVideoRepository;
            _youtubeService = youtubeService;
            _clock = clock;
        }

        public async Task RefreshVideosAsync(StaleChannelModel channel)
        {
            var now = _clock.UtcNow;
            var earliestPublishedAt = now.Subtract(Constants.VideoMaxAge);
            var videos = await _youtubeService.GetVideosAsync(
                channel.PlaylistId,
                earliestPublishedAt);
            var updateMaxAge = _clock.RandomDelay(
                Constants.ChannelMaxAgeMin,
                Constants.ChannelMaxAgeMax);

            await _channelVideoRepository.RefreshAsync(
                channel.Id,
                earliestPublishedAt,
                videos
                    .Where(video => video.ChannelId == channel.Id)
                    .Select(video => new ChannelVideoRecord
                    {
                        ChannelId = video.ChannelId,
                        Id = video.Id,
                        Title = video.Title,
                        Duration = video.Duration,
                        PublishedAt = video.PublishedAt,
                        Thumbnail = video.Thumbnail
                    })
                    .ToList(),
                now.Add(updateMaxAge));
        }
    }
}
