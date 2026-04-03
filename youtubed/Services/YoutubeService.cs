using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Options;
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
                request.Fields = "nextPageToken,items(snippet(resourceId(kind, videoId),channelId,title,description,thumbnails(medium,default)),contentDetails(videoPublishedAt))";
                if (nextPageToken != null)
                {
                    request.PageToken = nextPageToken;
                }

                var response = await request.ExecuteAsync();
                nextPageToken = response.NextPageToken;
                var itemsToEnrich = new List<(PlaylistItem Item, DateTimeOffset PublishedAt)>();
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

                    var publishedAt = item.ContentDetails.VideoPublishedAtDateTimeOffset;
                    if (publishedAt == null || publishedAt < publishedAfter)
                    {
                        // Stop after this page. We might as well finish
                        // reading the current page since we already paid for
                        // the API call with our quota.
                        nextPageToken = null;
                        continue;
                    }

                    itemsToEnrich.Add((item, publishedAt.Value));
                }

                var durationsById = await GetDurationsByIdAsync(
                    itemsToEnrich
                        .Select(value => value.Item.Snippet.ResourceId.VideoId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToList());

                foreach (var (item, publishedAt) in itemsToEnrich)
                {
                    var videoId = item.Snippet.ResourceId.VideoId;
                    if (!durationsById.TryGetValue(videoId, out var duration))
                    {
                        continue;
                    }

                    results.Add(new YoutubeVideo
                    {
                        ChannelId = item.Snippet.ChannelId,
                        Id = videoId,
                        Title = item.Snippet.Title,
                        Duration = duration,
                        PublishedAt = publishedAt,
                        Thumbnail = PickThumbnail(item.Snippet.Thumbnails)
                    });
                }
            } while (nextPageToken != null);

            return results;
        }

        private async Task<IReadOnlyDictionary<string, TimeSpan>> GetDurationsByIdAsync(IReadOnlyCollection<string> videoIds)
        {
            if (videoIds.Count == 0)
            {
                return new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            }

            var request = Service.Videos.List("contentDetails");
            request.Fields = "items(id,contentDetails(duration))";
            request.Id = string.Join(",", videoIds);

            var response = await request.ExecuteAsync();
            return YoutubeVideoDurationParser.ParseById(
                response.Items.Select(item => new KeyValuePair<string, string>(
                    item.Id,
                    item.ContentDetails?.Duration)));
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
            var service = new YouTubeService(new BaseClientService.Initializer
            {
                ApiKey = _options.Credentials,
                ApplicationName = $"subscriptiond/{AppVersion.Current}"
            });
            return service;
        }
    }
}
