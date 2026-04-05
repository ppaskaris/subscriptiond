using System;
using System.Globalization;
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
        public string WatchUrl =>
            string.IsNullOrWhiteSpace(VideoTitle)
                ? $"/watch/{VideoId}"
                : $"/watch/{VideoId}?title={WebUtility.UrlEncode(VideoTitle)}";

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
