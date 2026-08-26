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
        private readonly object _sync = new object();
        private int _getChannelsByIdCallCount;
        private int _getPlaylistVideosCallCount;
        private int _getVideoDurationsCallCount;

        public int GetChannelCallCount { get; private set; }

        public int GetChannelByIdCallCount { get; private set; }

        public int GetChannelsByIdCallCount => Volatile.Read(ref _getChannelsByIdCallCount);

        public int GetVideoChannelCallCount { get; private set; }

        public int GetPlaylistVideosCallCount => Volatile.Read(ref _getPlaylistVideosCallCount);

        public int GetVideoDurationsCallCount => Volatile.Read(ref _getVideoDurationsCallCount);

        public string LastChannelUrl { get; private set; }

        public string LastChannelId { get; private set; }

        public IReadOnlyCollection<string> LastChannelIds { get; private set; }

        public IReadOnlyCollection<string> LastVideoDurationIds { get; private set; }

        public IReadOnlyList<IReadOnlyCollection<string>> VideoDurationRequestIds { get; private set; } =
            new List<IReadOnlyCollection<string>>();

        public string LastPlaylistPageToken { get; private set; }

        public Action BeforePlaylistPageResponse { get; set; }

        public Func<string, string, CancellationToken, Task> BeforePlaylistPageResponseAsync { get; set; }

        public Action<int> BeforeDurationResponse { get; set; }

        public Func<IReadOnlyCollection<string>, CancellationToken, Task> BeforeDurationResponseAsync { get; set; }

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
            Interlocked.Increment(ref _getChannelsByIdCallCount);
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

        public async Task<YoutubePlaylistVideoPage> GetPlaylistVideoPageAsync(
            string playlistId,
            string pageToken,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _getPlaylistVideosCallCount);
            LastPlaylistPageToken = pageToken;
            BeforePlaylistPageResponse?.Invoke();
            if (BeforePlaylistPageResponseAsync != null)
            {
                await BeforePlaylistPageResponseAsync(playlistId, pageToken, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pagesByPlaylist.TryGetValue(playlistId, out var pages))
            {
                return new YoutubePlaylistVideoPage();
            }

            int index;
            lock (_sync)
            {
                index = pageToken == null
                    ? 0
                    : _pageIndexesByPlaylist.TryGetValue(playlistId, out var savedIndex)
                        ? savedIndex
                        : 0;
                _pageIndexesByPlaylist[playlistId] = index + 1;
            }

            if (index >= pages.Count)
            {
                return new YoutubePlaylistVideoPage();
            }

            return pages[index];
        }

        public async Task<IReadOnlyDictionary<string, TimeSpan>> GetVideoDurationsByIdAsync(
            IReadOnlyCollection<string> videoIds,
            CancellationToken cancellationToken)
        {
            var callCount = Interlocked.Increment(ref _getVideoDurationsCallCount);
            LastVideoDurationIds = videoIds.ToList();
            lock (_sync)
            {
                VideoDurationRequestIds = VideoDurationRequestIds
                    .Concat(new[] { videoIds.ToList() })
                    .ToList();
            }
            BeforeDurationResponse?.Invoke(callCount);
            if (BeforeDurationResponseAsync != null)
            {
                await BeforeDurationResponseAsync(videoIds, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var durations = _videosByPlaylist
                .SelectMany(value => value.Value)
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionary(video => video.Id, video => video.Duration, StringComparer.Ordinal);
            return durations;
        }

    }
}
