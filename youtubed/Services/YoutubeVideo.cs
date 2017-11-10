using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class YoutubeVideo
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTimeOffset PublishedAt { get; set; }
        public string Thumbnail { get; set; }
        public string ChannelId { get; set; }
    }
}
