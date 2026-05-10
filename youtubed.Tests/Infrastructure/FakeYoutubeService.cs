using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    public sealed class FakeYoutubeService : IYoutubeService
    {
        private readonly Dictionary<string, YoutubeChannel> _channelsByUrl =
            new Dictionary<string, YoutubeChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, YoutubeChannel> _channelsById =
            new Dictionary<string, YoutubeChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, YoutubeChannel> _videoChannelsByUrl =
            new Dictionary<string, YoutubeChannel>(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<YoutubeVideo>> _videosByPlaylist =
            new Dictionary<string, IReadOnlyList<YoutubeVideo>>(StringComparer.Ordinal);

        public int GetChannelCallCount { get; private set; }

        public int GetChannelByIdCallCount { get; private set; }

        public int GetVideoChannelCallCount { get; private set; }

        public int GetVideosCallCount { get; private set; }

        public DateTimeOffset? LastPublishedAfter { get; private set; }

        public string LastChannelUrl { get; private set; }

        public string LastChannelId { get; private set; }

        public void SetChannel(string url, YoutubeChannel channel)
        {
            _channelsByUrl[url] = channel;
            if (!string.IsNullOrWhiteSpace(channel?.Id))
            {
                _channelsById[channel.Id] = channel;
            }
        }

        public void SetChannelById(string id, YoutubeChannel channel)
        {
            _channelsById[id] = channel;
        }

        public void SetVideoChannel(string url, YoutubeChannel channel)
        {
            _videoChannelsByUrl[url] = channel;
        }

        public void SetVideos(string playlistId, params YoutubeVideo[] videos)
        {
            _videosByPlaylist[playlistId] = videos.ToList();
        }

        public Task<YoutubeChannel> GetChannelByUrlAsync(string url)
        {
            GetChannelCallCount++;
            LastChannelUrl = url;
            _channelsByUrl.TryGetValue(url, out var channel);
            return Task.FromResult(channel);
        }

        public Task<YoutubeChannel> GetChannelByIdAsync(string id)
        {
            GetChannelByIdCallCount++;
            LastChannelId = id;
            _channelsById.TryGetValue(id, out var channel);
            return Task.FromResult(channel);
        }

        public Task<YoutubeChannel> GetVideoChannelAsync(string url)
        {
            GetVideoChannelCallCount++;
            _videoChannelsByUrl.TryGetValue(url, out var channel);
            return Task.FromResult(channel);
        }

        public Task<IEnumerable<YoutubeVideo>> GetVideosAsync(string playlistId, DateTimeOffset publishedAfter)
        {
            GetVideosCallCount++;
            LastPublishedAfter = publishedAfter;
            if (_videosByPlaylist.TryGetValue(playlistId, out var videos))
            {
                return Task.FromResult<IEnumerable<YoutubeVideo>>(videos);
            }

            return Task.FromResult(Enumerable.Empty<YoutubeVideo>());
        }
    }
}
