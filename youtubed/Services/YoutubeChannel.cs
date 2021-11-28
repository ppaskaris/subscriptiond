using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class YoutubeChannel
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }

        public string PlaylistId { get; set; }
    }
}
