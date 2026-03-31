using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class StaleChannelModel
    {
        public string Id { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public string Thumbnail { get; set; }
        public string PlaylistId { get; set; }
    }
}
