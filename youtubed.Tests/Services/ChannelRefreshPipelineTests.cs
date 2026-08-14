using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class ChannelRefreshPipelineTests
    {
        [Fact]
        public async Task RefreshAsync_RejectsBatchLargerThanWorkerBound()
        {
            var pipeline = CreatePipeline(new RecordingChannelRepository(), new FakeYoutubeService());
            var ids = Enumerable.Range(0, Constants.ChannelRefreshBatchSize + 1)
                .Select(index => $"channel-{index}")
                .ToList();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => pipeline.RefreshAsync(ids, CancellationToken.None));
        }

        [Fact]
        public async Task RefreshAsync_LoadsOnlyExplicitDistinctIds()
        {
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1");
            repository.Add("channel-2");
            repository.Add("not-requested");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2"));
            var pipeline = CreatePipeline(repository, youtube);

            var result = await pipeline.RefreshAsync(
                new[] { "channel-2", "channel-1", "channel-2" },
                CancellationToken.None);

            Assert.Equal(new[] { "channel-2", "channel-1" }, repository.LastBatchIds);
            Assert.Equal(2, result.SelectedChannelCount);
            Assert.Equal(new[] { "channel-1", "channel-2" },
                repository.SavedResults.Select(value => value.Channel.Id).OrderBy(id => id));
        }

        [Fact]
        public async Task RefreshAsync_CancellationBeforeYoutubeCallDoesNotPersist()
        {
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1");
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await CreatePipeline(repository, new FakeYoutubeService())
                .RefreshAsync(new[] { "channel-1" }, cancellation.Token);

            Assert.True(result.CanceledBeforeStartingYoutubeCall);
            Assert.Empty(repository.SavedResults);
        }

        [Fact]
        public async Task RefreshAsync_CancellationAfterMetadataPersistsCompletedMetadata()
        {
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1", "playlist-1");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "updated.png",
                PlaylistId = "playlist-1"
            });
            var cancellation = new CancellationTokenSource();
            var pipeline = CreatePipeline(
                repository,
                youtube,
                new CancelingDelay(cancellation));

            var result = await pipeline.RefreshAsync(new[] { "channel-1" }, cancellation.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            var saved = Assert.Single(repository.SavedResults);
            Assert.Equal("Updated", saved.Channel.Title);
            Assert.False(saved.VideosRefreshed);
        }

        [Fact]
        public async Task RefreshAsync_CancellationBetweenPlaylistPagesStopsNewCallsAndPersistsMetadata()
        {
            using var cancellation = new CancellationTokenSource();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1", "playlist-1");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "updated.png",
                PlaylistId = "playlist-1"
            });
            youtube.SetPlaylistPages(
                "playlist-1",
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-2",
                    Videos = CreateVideos("channel-1", 0, 1, now)
                },
                new YoutubePlaylistVideoPage
                {
                    Videos = CreateVideos("channel-1", 1, 1, now)
                });
            var pipeline = CreatePipeline(
                repository,
                youtube,
                new CancelAfterCountDelay(cancellation, 2),
                new FakeAppClock { UtcNow = now });

            var result = await pipeline.RefreshAsync(new[] { "channel-1" }, cancellation.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(1, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(0, youtube.GetVideoDurationsCallCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.Equal("Updated", saved.Channel.Title);
            Assert.False(saved.VideosRefreshed);
        }

        [Fact]
        public async Task RefreshAsync_CancellationDuringPlaylistRequestPersistsPriorMetadata()
        {
            using var cancellation = new CancellationTokenSource();
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1");
            repository.Add("channel-2", "playlist-2");
            var youtube = new FakeYoutubeService
            {
                BeforePlaylistPageResponse = cancellation.Cancel
            };
            youtube.SetChannelById("channel-1", Metadata("channel-1"));
            youtube.SetChannelById("channel-2", new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Updated channel-2",
                Thumbnail = "channel-2.png",
                PlaylistId = "playlist-2"
            });
            var pipeline = CreatePipeline(repository, youtube);

            var result = await pipeline.RefreshAsync(
                new[] { "channel-1", "channel-2" },
                cancellation.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(2, repository.SavedResults.Count);
            Assert.All(repository.SavedResults, saved => Assert.False(saved.VideosRefreshed));
            Assert.Contains(repository.SavedResults, saved => saved.Channel.Id == "channel-1");
            Assert.Contains(repository.SavedResults, saved => saved.Channel.Id == "channel-2");
        }

        [Fact]
        public async Task RefreshAsync_CancellationBetweenDurationChunksPersistsCompletedChannelOnly()
        {
            using var cancellation = new CancellationTokenSource();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1", "playlist-1");
            repository.Add("channel-2", "playlist-2");
            var youtube = new FakeYoutubeService
            {
                BeforeDurationResponse = callCount =>
                {
                    if (callCount == 2)
                    {
                        cancellation.Cancel();
                    }
                }
            };
            youtube.SetChannelById("channel-1", PlaylistMetadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", PlaylistMetadata("channel-2", "playlist-2"));
            youtube.SetVideos("playlist-1", CreateVideos("channel-1", 0, 50, now).ToArray());
            youtube.SetVideos("playlist-2", CreateVideos("channel-2", 50, 1, now).ToArray());
            var pipeline = CreatePipeline(
                repository,
                youtube,
                clock: new FakeAppClock
                {
                    UtcNow = now,
                    RandomDelayValue = TimeSpan.FromMinutes(60)
                });

            var result = await pipeline.RefreshAsync(
                new[] { "channel-1", "channel-2" },
                cancellation.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(1, result.RefreshedChannelCount);
            var completed = Assert.Single(repository.SavedResults, saved => saved.VideosRefreshed);
            Assert.Equal("channel-1", completed.Channel.Id);
            Assert.Equal(50, completed.Channel.Videos.Count);
            var unfinished = Assert.Single(repository.SavedResults, saved => saved.Channel.Id == "channel-2");
            Assert.False(unfinished.VideosRefreshed);
            Assert.Empty(unfinished.Channel.Videos);
        }

        [Fact]
        public async Task RefreshAsync_MixedBatchPersistsCompletedFallbackButNotUnfinishedPlaylist()
        {
            using var cancellation = new CancellationTokenSource();
            var repository = new RecordingChannelRepository();
            repository.Add("channel-1");
            repository.Add("channel-2", "playlist-2");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1"));
            var pipeline = CreatePipeline(repository, youtube, new CancelingDelay(cancellation));

            var result = await pipeline.RefreshAsync(
                new[] { "channel-1", "channel-2" },
                cancellation.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            var saved = Assert.Single(repository.SavedResults);
            Assert.Equal("channel-1", saved.Channel.Id);
            Assert.DoesNotContain(repository.SavedResults, value => value.Channel.Id == "channel-2");
        }

        private static ChannelRefreshPipeline CreatePipeline(
            RecordingChannelRepository repository,
            FakeYoutubeService youtube,
            IYoutubeCallDelay delay = null,
            FakeAppClock clock = null)
        {
            return new ChannelRefreshPipeline(
                repository,
                youtube,
                clock ?? new FakeAppClock(),
                delay ?? new ImmediateDelay());
        }

        private static YoutubeChannel Metadata(string id) => new YoutubeChannel
        {
            Id = id,
            Title = $"Updated {id}",
            Thumbnail = $"{id}.png"
        };

        private static YoutubeChannel PlaylistMetadata(string id, string playlistId) => new YoutubeChannel
        {
            Id = id,
            Title = $"Updated {id}",
            Thumbnail = $"{id}.png",
            PlaylistId = playlistId
        };

        private static IReadOnlyList<YoutubeVideo> CreateVideos(
            string channelId,
            int start,
            int count,
            DateTimeOffset publishedAt)
        {
            return Enumerable.Range(start, count)
                .Select(index => new YoutubeVideo
                {
                    ChannelId = channelId,
                    Id = $"video-{index:00}",
                    Title = $"Video {index}",
                    Duration = TimeSpan.FromMinutes(index + 1),
                    PublishedAt = publishedAt.AddMinutes(-index),
                    Thumbnail = $"video-{index:00}.png"
                })
                .ToList();
        }

        private sealed class RecordingChannelRepository : IChannelRepository
        {
            private readonly Dictionary<string, Channel> _channels =
                new Dictionary<string, Channel>(StringComparer.Ordinal);

            public IReadOnlyList<string> LastBatchIds { get; private set; } = Array.Empty<string>();
            public IReadOnlyList<ChannelRefreshResult> SavedResults { get; private set; } =
                Array.Empty<ChannelRefreshResult>();

            public void Add(string id, string playlistId = null)
            {
                _channels[id] = new Channel
                {
                    Id = id,
                    Url = string.Format(Constants.YoutubeChannelUrl, id),
                    Title = id,
                    Thumbnail = $"{id}.png",
                    PlaylistId = playlistId,
                    StaleAfter = DateTimeOffset.MinValue
                };
            }

            public Task<IReadOnlyList<Channel>> GetBatchAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken)
            {
                LastBatchIds = channelIds.ToList();
                return Task.FromResult<IReadOnlyList<Channel>>(
                    channelIds.Where(_channels.ContainsKey).Select(id => _channels[id]).ToList());
            }

            public Task SaveRefreshResultsAsync(
                IReadOnlyCollection<ChannelRefreshResult> results,
                CancellationToken cancellationToken)
            {
                SavedResults = results.ToList();
                return Task.CompletedTask;
            }

            public Task<Channel> GetByIdAsync(string id) => throw new NotImplementedException();
            public Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter) => throw new NotImplementedException();
            public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now) => throw new NotImplementedException();
        }

        private sealed class ImmediateDelay : IYoutubeCallDelay
        {
            public Task DelayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class CancelingDelay : IYoutubeCallDelay
        {
            private readonly CancellationTokenSource _cancellation;

            public CancelingDelay(CancellationTokenSource cancellation)
            {
                _cancellation = cancellation;
            }

            public Task DelayAsync(CancellationToken cancellationToken)
            {
                _cancellation.Cancel();
                return Task.FromCanceled(cancellationToken);
            }
        }

        private sealed class CancelAfterCountDelay : IYoutubeCallDelay
        {
            private readonly CancellationTokenSource _cancellation;
            private readonly int _cancelAfter;
            private int _count;

            public CancelAfterCountDelay(CancellationTokenSource cancellation, int cancelAfter)
            {
                _cancellation = cancellation;
                _cancelAfter = cancelAfter;
            }

            public Task DelayAsync(CancellationToken cancellationToken)
            {
                _count++;
                if (_count >= _cancelAfter)
                {
                    _cancellation.Cancel();
                    return Task.FromCanceled(cancellationToken);
                }

                return Task.CompletedTask;
            }
        }
    }
}
