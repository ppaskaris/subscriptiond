using System;

namespace youtubed.Domain
{
    public class ChannelVideo
    {
        public string VideoId { get; set; }
        public string ChannelId { get; set; }
        public string Title { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string ThumbnailUrl { get; set; }
    }
}
