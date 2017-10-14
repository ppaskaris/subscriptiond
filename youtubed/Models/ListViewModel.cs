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
        public DateTimeOffset ExpiredAfter { get; set; }
        public IEnumerable<VideoViewModel> Videos { get; set; }
        public int StaleCount { get; set; }

        public TimeSpan MaxAge => ExpiredAfter.Subtract(DateTimeOffset.Now);
    }
}
