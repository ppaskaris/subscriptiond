using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.PlatformAbstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class YoutubeService : IYoutubeService
    {
        private readonly YoutubeOptions _options;
        private readonly Lazy<YouTubeService> _service;

        public YoutubeService(IOptions<YoutubeOptions> options)
        {
            _options = options.Value;
            _service = new Lazy<YouTubeService>(CreateService);
        }

        private YouTubeService Service => _service.Value;

        public async Task<YoutubeChannel> GetChannelAsync(string url)
        {
            var match = Constants.YoutubeExpression.Match(url);
            if (!match.Success)
            {
                throw new ArgumentException(nameof(url), "Invalid format.");
            }

            var request = Service.Channels.List("id,snippet");
            request.MaxResults = 1;

            var type = match.Groups[1].Value;
            var identifier = match.Groups[2].Value;
            switch (type)
            {
                case "channel":
                    request.Id = identifier;
                    break;
                case "user":
                    request.ForUsername = identifier;
                    break;
                default:
                    throw new ArgumentException(nameof(url), "Invalid format.");
            }

            var response = await request.ExecuteAsync();
            var item = response.Items.FirstOrDefault();
            if (item == null)
            {
                return null;
            }

            return new YoutubeChannel
            {
                Id = item.Id,
                Title = item.Snippet?.Title,
                Description = item.Snippet?.Description,
                Thumbnail = item.Snippet?.Thumbnails?.Default__?.Url
            };
        }

        private YouTubeService CreateService()
        {
            var version = PlatformServices.Default.Application.ApplicationVersion;
            var service = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _options.Credentials,
                ApplicationName = $"youtubed/{version}"
            });
            return service;
        }
    }
}
