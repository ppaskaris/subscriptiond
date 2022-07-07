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
            var match = Constants.YoutubeChannelExpression.Match(url);
            if (!match.Success)
            {
                throw new ArgumentException(nameof(url), "Invalid format.");
            }

            var type = match.Groups[1].Value;
            var identifier = match.Groups[2].Value;

            return await GetChannelByIdentifierAsync(type, identifier);
        }

        private async Task<YoutubeChannel> GetChannelByIdentifierAsync(string type, string identifier)
        {
            var request = Service.Channels.List("id,snippet,contentDetails");
            request.MaxResults = 1;
            request.Fields = "items(id,snippet(title,thumbnails(medium,default)),contentDetails(relatedPlaylists(uploads)))";

            switch (type)
            {
                case "channel":
                    request.Id = identifier;
                    break;
                case "user":
                    request.ForUsername = identifier;
                    break;
                default:
                    throw new ArgumentException("url", "Invalid format.");
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
                Thumbnail = PickThumbnail(item.Snippet.Thumbnails),
                PlaylistId = item.ContentDetails.RelatedPlaylists.Uploads
            };
        }

        public async Task<YoutubeChannel> GetVideoChannelAsync(string url)
        {
            var match = Constants.YoutubeVideoExpression.Match(url);
            if (!match.Success)
            {
                throw new ArgumentException(nameof(url), "Invalid format.");
            }

            var identifier = match.Groups[1].Value;

            var request = Service.Videos.List("snippet");
            request.MaxResults = 1;
            request.Fields = "items(snippet(channelId))";
            request.Id = identifier;

            var response = await request.ExecuteAsync();
            var item = response.Items.FirstOrDefault();
            if (item == null)
            {
                return null;
            }

            return await GetChannelByIdentifierAsync("channel", item.Snippet.ChannelId);
        }

        public async Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string playlistId, DateTimeOffset publishedAfter)
        {
            string nextPageToken = null;
            var results = new List<YoutubeVideo>();

            do
            {
                var request = Service.PlaylistItems.List("snippet,contentDetails");
                request.PlaylistId = playlistId;
                request.MaxResults = 50;
                request.Fields = "nextPageToken,items(snippet(resourceId(kind, videoId),channelId,title,thumbnails(medium,default)),contentDetails(videoPublishedAt))";
                if (nextPageToken != null)
                {
                    request.PageToken = nextPageToken;
                }

                var response = await request.ExecuteAsync();
                nextPageToken = response.NextPageToken;
                foreach (var item in response.Items)
                {
                    if (item.Snippet.ResourceId.Kind != "youtube#video")
                    {
                        continue;
                    }

                    //
                    // Snippet.PublishedAt is the time the video was added to
                    // the uploads playlist.
                    // 
                    // ContentDetails.VideoPublishedAt is the time the video
                    // was published to YouTube.
                    //

                    DateTimeOffset publishedAt =
                        new DateTimeOffset(item.ContentDetails.VideoPublishedAt.Value);
                    if (publishedAt < publishedAfter)
                    {
                        // Stop after this page. We might as well finish
                        // reading the current page since we already paid for
                        // the API call with our quota.
                        nextPageToken = null;
                        continue;
                    }

                    var result = new YoutubeVideo
                    {
                        ChannelId = item.Snippet.ChannelId,
                        Id = item.Snippet.ResourceId.VideoId,
                        Title = item.Snippet.Title,
                        Duration = TimeSpan.FromMinutes(5),
                        PublishedAt = publishedAt,
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
