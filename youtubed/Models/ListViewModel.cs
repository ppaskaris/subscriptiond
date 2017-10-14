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
        public DateTimeOffset ExpiredAfter { get; set; }
        public int StaleCount { get; set; }
        public IEnumerable<VideoViewModel> Videos { get; set; }
        public IEnumerable<ChannelModel> Channels { get; internal set; }

        public TimeSpan MaxAge => ExpiredAfter.Subtract(DateTimeOffset.Now);
    }
}
