using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IYoutubeService
    {
        Task<YoutubeChannel> GetChannelAsync(string url);

        Task<YoutubeChannel> GetVideoChannelAsync(string url);

        Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string playlistId, DateTimeOffset publishedAfter);
    }
}
