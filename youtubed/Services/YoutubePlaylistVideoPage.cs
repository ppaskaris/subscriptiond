using System.Collections.Generic;

namespace youtubed.Services
{
    public sealed class YoutubePlaylistVideoPage
    {
        public IReadOnlyList<YoutubeVideo> Videos { get; set; } = new List<YoutubeVideo>();
        public string NextPageToken { get; set; }
    }
}
