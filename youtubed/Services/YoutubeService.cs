using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        public async Task<YoutubeChannel> GetChannelByUrlAsync(string url)
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

        public Task<YoutubeChannel> GetChannelByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(nameof(id), "Invalid format.");
            }

            return GetChannelByIdentifierAsync("channel", id);
        }

        public async Task<IReadOnlyDictionary<string, YoutubeChannel>> GetChannelsByIdAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken)
        {
            var normalizedIds = ids
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalizedIds.Count == 0)
            {
                return new Dictionary<string, YoutubeChannel>(StringComparer.Ordinal);
            }

            var request = Service.Channels.List("id,snippet,contentDetails");
            request.MaxResults = normalizedIds.Count;
            request.Fields = "items(id,snippet(title,thumbnails(medium,default)),contentDetails(relatedPlaylists(uploads)))";
            request.Id = string.Join(",", normalizedIds);

            var response = await request.ExecuteAsync(cancellationToken);
            return response.Items.ToDictionary(
                item => item.Id,
                item => new YoutubeChannel
                {
                    Id = item.Id,
                    Title = item.Snippet.Title,
                    Thumbnail = PickThumbnail(item.Snippet.Thumbnails),
                    PlaylistId = item.ContentDetails.RelatedPlaylists.Uploads
                },
                StringComparer.Ordinal);
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

        public async Task<IEnumerable<YoutubeVideo>> GetPlaylistVideosAsync(string playlistId, DateTimeOffset publishedAfter)
        {
            string nextPageToken = null;
            var results = new List<YoutubeVideo>();

            do
            {
                var page = await GetPlaylistVideoPageAsync(
                    playlistId,
                    publishedAfter,
                    nextPageToken,
                    CancellationToken.None);
                results.AddRange(page.Videos);
                nextPageToken = page.NextPageToken;
            } while (nextPageToken != null);

            return results;
        }

        public async Task<YoutubePlaylistVideoPage> GetPlaylistVideoPageAsync(
            string playlistId,
            DateTimeOffset publishedAfter,
            string pageToken,
            CancellationToken cancellationToken)
        {
            var request = Service.PlaylistItems.List("snippet,contentDetails");
            request.PlaylistId = playlistId;
            request.MaxResults = 50;
            request.Fields = "nextPageToken,items(snippet(resourceId(kind, videoId),channelId,title,description,thumbnails(medium,default)),contentDetails(videoPublishedAt))";
            if (pageToken != null)
            {
                request.PageToken = pageToken;
            }

            var response = await request.ExecuteAsync(cancellationToken);
            var nextPageToken = response.NextPageToken;
            var videos = new List<YoutubeVideo>();
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
                    // Stop after this page. We might as well finish reading
                    // the current page since we already paid for the API call.
                    nextPageToken = null;
                    continue;
                }

                videos.Add(new YoutubeVideo
                {
                    ChannelId = item.Snippet.ChannelId,
                    Id = item.Snippet.ResourceId.VideoId,
                    Title = item.Snippet.Title,
                    PublishedAt = publishedAt.Value,
                    Thumbnail = PickThumbnail(item.Snippet.Thumbnails)
                });
            }

            return new YoutubePlaylistVideoPage
            {
                Videos = videos,
                NextPageToken = nextPageToken
            };
        }

        public async Task<IReadOnlyDictionary<string, TimeSpan>> GetVideoDurationsByIdAsync(
            IReadOnlyCollection<string> videoIds,
            CancellationToken cancellationToken)
        {
            var normalizedIds = videoIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (normalizedIds.Count == 0)
            {
                return new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            }

            if (normalizedIds.Count > 50)
            {
                throw new ArgumentException("At most 50 video ids can be fetched in one YouTube API call.", nameof(videoIds));
            }

            var request = Service.Videos.List("contentDetails");
            request.Fields = "items(id,contentDetails(duration))";
            request.Id = string.Join(",", normalizedIds);

            var response = await request.ExecuteAsync(cancellationToken);
            return YoutubeVideoDurationParser.ParseById(
                response.Items.Select(item => new KeyValuePair<string, string>(
                    item.Id,
                    item.ContentDetails?.Duration)));
        }

        public async Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string playlistId, DateTimeOffset publishedAfter)
        {
            var videos = (await GetPlaylistVideosAsync(playlistId, publishedAfter)).ToList();
            var durationsById = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            foreach (var chunk in videos
                .Select(video => video.Id)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Chunk(50))
            {
                foreach (var duration in await GetVideoDurationsByIdAsync(chunk, CancellationToken.None))
                {
                    durationsById[duration.Key] = duration.Value;
                }
            }

            return videos
                .Where(video => durationsById.ContainsKey(video.Id))
                .Select(video =>
                {
                    video.Duration = durationsById[video.Id];
                    return video;
                })
                .ToList();
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
