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
        public int StaleCount { get; set; }
        public IEnumerable<VideoViewModel> Videos { get; set; }
        public IEnumerable<ChannelModel> Channels { get; set; }

        public TimeSpan MaxAge { get; set; }
        public TimeSpan StaleRefreshAfter { get; set; }
    }
}
