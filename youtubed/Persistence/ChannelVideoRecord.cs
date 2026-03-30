using System;

namespace youtubed.Persistence
{
    public class ChannelVideoRecord
    {
        public string ChannelId { get; set; }
        public string Id { get; set; }
        public string Title { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Thumbnail { get; set; }
    }
}
