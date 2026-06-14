using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IYoutubeService
    {
        Task<YoutubeChannel> GetChannelByUrlAsync(string url);

        Task<YoutubeChannel> GetChannelByIdAsync(string id);

        Task<IReadOnlyDictionary<string, YoutubeChannel>> GetChannelsByIdAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken);

        Task<YoutubeChannel> GetVideoChannelAsync(string url);

        Task<YoutubePlaylistVideoPage> GetPlaylistVideoPageAsync(
            string playlistId,
            DateTimeOffset publishedAfter,
            string pageToken,
            CancellationToken cancellationToken);

        Task<IEnumerable<YoutubeVideo>> GetPlaylistVideosAsync(string playlistId, DateTimeOffset publishedAfter);

        Task<IReadOnlyDictionary<string, TimeSpan>> GetVideoDurationsByIdAsync(
            IReadOnlyCollection<string> videoIds,
            CancellationToken cancellationToken);

        Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string playlistId, DateTimeOffset publishedAfter);
    }
}
