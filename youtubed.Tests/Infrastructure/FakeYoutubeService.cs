using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private readonly Dictionary<string, IReadOnlyList<YoutubePlaylistVideoPage>> _pagesByPlaylist =
            new Dictionary<string, IReadOnlyList<YoutubePlaylistVideoPage>>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pageIndexesByPlaylist =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public int GetChannelCallCount { get; private set; }

        public int GetChannelByIdCallCount { get; private set; }

        public int GetChannelsByIdCallCount { get; private set; }

        public int GetVideoChannelCallCount { get; private set; }

        public int GetPlaylistVideosCallCount { get; private set; }

        public int GetVideoDurationsCallCount { get; private set; }

        public string LastChannelUrl { get; private set; }

        public string LastChannelId { get; private set; }

        public IReadOnlyCollection<string> LastChannelIds { get; private set; }

        public IReadOnlyCollection<string> LastVideoDurationIds { get; private set; }

        public IReadOnlyList<IReadOnlyCollection<string>> VideoDurationRequestIds { get; private set; } =
            new List<IReadOnlyCollection<string>>();

        public string LastPlaylistPageToken { get; private set; }

        public Action BeforePlaylistPageResponse { get; set; }

        public Action<int> BeforeDurationResponse { get; set; }

        public Exception ChannelsByIdException { get; set; }

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
            _pagesByPlaylist[playlistId] = new[]
            {
                new YoutubePlaylistVideoPage
                {
                    Videos = videos.ToList()
                }
            };
        }

        public void SetPlaylistPages(string playlistId, params YoutubePlaylistVideoPage[] pages)
        {
            _pagesByPlaylist[playlistId] = pages.ToList();
            _videosByPlaylist[playlistId] = pages
                .SelectMany(page => page.Videos)
                .ToList();
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

        public Task<IReadOnlyDictionary<string, YoutubeChannel>> GetChannelsByIdAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken cancellationToken)
        {
            GetChannelsByIdCallCount++;
            if (ChannelsByIdException != null)
            {
                throw ChannelsByIdException;
            }
            LastChannelIds = ids.ToList();
            var channels = ids
                .Where(id => _channelsById.ContainsKey(id))
                .ToDictionary(id => id, id => _channelsById[id], StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, YoutubeChannel>>(channels);
        }

        public Task<YoutubeChannel> GetVideoChannelAsync(string url)
        {
            GetVideoChannelCallCount++;
            _videoChannelsByUrl.TryGetValue(url, out var channel);
            return Task.FromResult(channel);
        }

        public Task<YoutubePlaylistVideoPage> GetPlaylistVideoPageAsync(
            string playlistId,
            string pageToken,
            CancellationToken cancellationToken)
        {
            GetPlaylistVideosCallCount++;
            LastPlaylistPageToken = pageToken;
            BeforePlaylistPageResponse?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pagesByPlaylist.TryGetValue(playlistId, out var pages))
            {
                return Task.FromResult(new YoutubePlaylistVideoPage());
            }

            var index = pageToken == null
                ? 0
                : _pageIndexesByPlaylist.TryGetValue(playlistId, out var savedIndex)
                    ? savedIndex
                    : 0;
            _pageIndexesByPlaylist[playlistId] = index + 1;

            if (index >= pages.Count)
            {
                return Task.FromResult(new YoutubePlaylistVideoPage());
            }

            return Task.FromResult(pages[index]);
        }

        public Task<IReadOnlyDictionary<string, TimeSpan>> GetVideoDurationsByIdAsync(
            IReadOnlyCollection<string> videoIds,
            CancellationToken cancellationToken)
        {
            GetVideoDurationsCallCount++;
            LastVideoDurationIds = videoIds.ToList();
            VideoDurationRequestIds = VideoDurationRequestIds
                .Concat(new[] { videoIds.ToList() })
                .ToList();
            BeforeDurationResponse?.Invoke(GetVideoDurationsCallCount);
            cancellationToken.ThrowIfCancellationRequested();
            var durations = _videosByPlaylist
                .SelectMany(value => value.Value)
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionary(video => video.Id, video => video.Duration, StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, TimeSpan>>(durations);
        }

    }
}
