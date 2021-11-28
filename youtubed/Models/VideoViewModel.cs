using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    }
}
