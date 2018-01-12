using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
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
            request.Fields = "items(id,snippet(title,thumbnails(medium,default)))";

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
                Title = item.Snippet.Title,
                Thumbnail = PickThumbnail(item.Snippet.Thumbnails)
            };
        }

        public async Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string channelId, DateTimeOffset publishedAfter)
        {
            string nextPageToken = null;
            var results = new List<YoutubeVideo>();

            do
            {
                var request = Service.Search.List("snippet");
                request.ChannelId = channelId;
                request.MaxResults = 50;
                request.Fields = "nextPageToken,items(id(kind,videoId),snippet(channelId,title,publishedAt,thumbnails(medium,default)))";
                request.Order = SearchResource.ListRequest.OrderEnum.Date;
                request.PublishedAfter = publishedAfter.UtcDateTime;
                request.SafeSearch = SearchResource.ListRequest.SafeSearchEnum.None;
                request.Type = "video";
                if (nextPageToken != null)
                {
                    request.PageToken = nextPageToken;
                }

                var response = await request.ExecuteAsync();
                nextPageToken = response.NextPageToken;
                foreach (var item in response.Items)
                {
                    if (item.Id.Kind != "youtube#video")
                    {
                        continue;
                    }

                    var result = new YoutubeVideo
                    {
                        ChannelId = item.Snippet.ChannelId,
                        Id = item.Id.VideoId,
                        Title = item.Snippet.Title,
                        Duration = TimeSpan.FromMinutes(5),
                        PublishedAt = new DateTimeOffset(item.Snippet.PublishedAt.Value),
                        Thumbnail = PickThumbnail(item.Snippet.Thumbnails)
                    };
                    results.Add(result);
                }
            } while (nextPageToken != null);

            return results;
        }

        private string PickThumbnail(ThumbnailDetails thumbnailDetails)
        {
            var thumbnail =
                thumbnailDetails.Medium ??
                thumbnailDetails.Default__;
            return thumbnail?.Url;
        }

        private YouTubeService CreateService()
        {
            var version = PlatformServices.Default.Application.ApplicationVersion;
            var service = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _options.Credentials,
                ApplicationName = $"subscriptiond/{version}"
            });
            return service;
        }
    }
}
