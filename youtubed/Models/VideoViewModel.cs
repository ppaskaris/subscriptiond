using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;

namespace youtubed.Models
{
    public class VideoViewModel
    {
        public string ChannelTitle { get; set; }
        public string ChannelUrl { get; set; }
        public string VideoId { get; set; }
        public string VideoTitle { get; set; }
        public TimeSpan VideoDuration { get; set; }
        public DateTimeOffset VideoPublishedAt { get; set; }
        public string VideoThumbnail { get; set; }

        public string VideoUrl => string.Format(Constants.YoutubeWatchUrl, VideoId);
        public string WatchUrl => GetWatchUrl();

        public string GetWatchUrl(decimal? playbackRate = null)
        {
            var url = $"/watch/{VideoId}";
            var query = new Dictionary<string, string>();

            if (!string.IsNullOrWhiteSpace(VideoTitle))
            {
                query["title"] = VideoTitle;
            }
            if (playbackRate != null)
            {
                query["playbackRate"] = Constants.FormatPlaybackRate(playbackRate.Value);
            }

            return query.Count == 0
                ? url
                : $"{url}?{string.Join("&", query.Select(item => $"{item.Key}={WebUtility.UrlEncode(item.Value)}"))}";
        }

        public string FormattedVideoDuration =>
            VideoDuration.TotalHours >= 1
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1:mm\\:ss}",
                    (int)VideoDuration.TotalHours,
                    VideoDuration)
                : VideoDuration.ToString("m\\:ss", CultureInfo.InvariantCulture);
    }
}
