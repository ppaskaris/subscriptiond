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

        public YoutubeService(IOptions<YoutubeOptions> options)
        {
            _options = options.Value;
        }

        public async Task<YoutubeChannel> GetChannelAsync(string url)
        {
            var version = PlatformServices.Default.Application.ApplicationVersion;
            var service = new YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
            {
                ApiKey = _options.Credentials,
                ApplicationName = $"youtubed/{version}"
            });

            var match = Constants.YoutubeExpression.Match(url);
            if (!match.Success)
            {
                throw new ArgumentException(nameof(url), "Invalid channel URL format.");
            }

            var listRequest = service.Channels.List("id,snippet");
            listRequest.MaxResults = 1;

            var type = match.Groups[1].Value;
            var identifier = match.Groups[2].Value;
            switch (type)
            {
                case "channel":
                    listRequest.Id = identifier;
                    break;
                case "user":
                    listRequest.ForUsername = identifier;
                    break;
                default:
                    throw new ArgumentException(nameof(url), "Invalid channel URL type.");
            }

            var listResponse = await listRequest.ExecuteAsync();
            var channel = listResponse.Items.FirstOrDefault();
            if (channel == null)
            {
                return null;
            }

            return new YoutubeChannel
            {
                Id = channel.Id,
                Title = channel.Snippet?.Title,
                Description = channel.Snippet?.Description,
                Thumbnail = channel.Snippet?.Thumbnails?.Default__?.Url
            };
        }
    }
}
