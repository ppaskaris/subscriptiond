using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class ListViewModel
    {
        public Guid Id { get; set; }
        public string Token { get; set; }
        public string Title { get; set; }
        public decimal PlaybackRate { get; set; } = Constants.DefaultListPlaybackRate;
        public DateTimeOffset ExpiredAfter { get; set; }
        public DateTimeOffset Now { get; set; }
        public int StaleCount { get; set; }
        public bool HasMoreVideos { get; set; }
        public IEnumerable<VideoViewModel> Videos { get; set; } = Enumerable.Empty<VideoViewModel>();
        public IEnumerable<ChannelModel> Channels { get; set; } = Enumerable.Empty<ChannelModel>();

        public TimeSpan MaxAge { get; set; }
    }
}
