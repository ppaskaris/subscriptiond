using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class ChannelRefreshPipelineTests
    {
        [Fact]
        public async Task RefreshStaleChannelsAsync_LoadsOnlyFirstBatchFromBoundedLookahead()
        {
            var repository = new FakeChannelRepository();
            var youtube = new FakeYoutubeService();
            for (var index = 0; index < 12; index++)
            {
                var id = $"channel-{index:00}";
                repository.AddStaleChannel(id, index);
                youtube.SetChannelById(id, new YoutubeChannel
                {
                    Id = id,
                    Title = $"Updated {index}",
                    Thumbnail = $"updated-{index}.png"
                });
            }

            var pipeline = CreatePipeline(repository, youtube);

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(12, result.StaleLookaheadCount);
            Assert.Equal(Constants.ChannelRefreshBatchSize, result.SelectedChannelCount);
            Assert.Equal(Constants.ChannelRefreshLookaheadCount, repository.LastLookaheadTake);
            Assert.Equal(repository.StaleIds.Take(Constants.ChannelRefreshBatchSize), repository.LastBatchIds);
            Assert.Equal(Constants.ChannelRefreshBatchSize, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_CancellationBeforePlaylistPersistsMetadataOnly()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "updated.png",
                PlaylistId = "playlist-1"
            });
            youtube.SetVideos(
                "playlist-1",
                new YoutubeVideo
                {
                    ChannelId = "channel-1",
                    Id = "video-1",
                    Title = "Video",
                    Duration = TimeSpan.FromMinutes(3),
                    PublishedAt = DateTimeOffset.UtcNow,
                    Thumbnail = "video.png"
                });
            var delay = new CancelingYoutubeCallDelay(cancellationTokenSource);
            var pipeline = CreatePipeline(repository, youtube, delay);

            var result = await pipeline.RefreshStaleChannelsAsync(cancellationTokenSource.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(0, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(0, youtube.GetVideoDurationsCallCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.False(saved.VideosRefreshed);
            Assert.Equal("Updated", saved.Channel.Title);
            Assert.Empty(saved.Channel.Videos);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_FetchesDurationsOnceAcrossBatch()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
            repository.AddStaleChannel("channel-2", 1, playlistId: "playlist-2");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "One",
                Thumbnail = "one.png",
                PlaylistId = "playlist-1"
            });
            youtube.SetChannelById("channel-2", new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Two",
                Thumbnail = "two.png",
                PlaylistId = "playlist-2"
            });
            youtube.SetVideos(
                "playlist-1",
                new YoutubeVideo
                {
                    ChannelId = "channel-1",
                    Id = "video-1",
                    Title = "Video 1",
                    Duration = TimeSpan.FromMinutes(4),
                    PublishedAt = now,
                    Thumbnail = "video-1.png"
                });
            youtube.SetVideos(
                "playlist-2",
                new YoutubeVideo
                {
                    ChannelId = "channel-2",
                    Id = "video-2",
                    Title = "Video 2",
                    Duration = TimeSpan.FromMinutes(5),
                    PublishedAt = now,
                    Thumbnail = "video-2.png"
                });
            var delay = new RecordingYoutubeCallDelay();
            var pipeline = CreatePipeline(repository, youtube, delay, new FakeAppClock
            {
                UtcNow = now,
                RandomDelayValue = TimeSpan.FromMinutes(60)
            });

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(1, youtube.GetChannelsByIdCallCount);
            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(1, youtube.GetVideoDurationsCallCount);
            Assert.Equal(3, delay.DelayCount);
            Assert.Equal(new[] { "video-1", "video-2" }, youtube.LastVideoDurationIds.OrderBy(id => id));
            Assert.Equal(2, result.RefreshedChannelCount);
            Assert.All(repository.SavedResults, saved => Assert.True(saved.VideosRefreshed));
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_DelaysBetweenPlaylistPagesAndDurationChunks()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "One",
                Thumbnail = "one.png",
                PlaylistId = "playlist-1"
            });
            youtube.SetPlaylistPages(
                "playlist-1",
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-2",
                    Videos = CreateVideos("channel-1", 0, 50, now)
                },
                new YoutubePlaylistVideoPage
                {
                    Videos = CreateVideos("channel-1", 50, 10, now)
                });
            var delay = new RecordingYoutubeCallDelay();
            var pipeline = CreatePipeline(repository, youtube, delay, new FakeAppClock
            {
                UtcNow = now,
                RandomDelayValue = TimeSpan.FromMinutes(60)
            });

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(2, youtube.GetVideoDurationsCallCount);
            Assert.Equal(4, delay.DelayCount);
            Assert.Equal(2, youtube.VideoDurationRequestIds.Count);
            Assert.Equal(50, youtube.VideoDurationRequestIds[0].Count);
            Assert.Equal(10, youtube.VideoDurationRequestIds[1].Count);
            Assert.Equal(1, result.RefreshedChannelCount);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_CancellationBetweenPlaylistPagesStopsNewYoutubeCalls()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
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
            var delay = new CancelingAfterDelayCountYoutubeCallDelay(cancellationTokenSource, 2);
            var pipeline = CreatePipeline(repository, youtube, delay, new FakeAppClock { UtcNow = now });

            var result = await pipeline.RefreshStaleChannelsAsync(cancellationTokenSource.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(1, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(0, youtube.GetVideoDurationsCallCount);
            Assert.Equal(0, result.RefreshedChannelCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.False(saved.VideosRefreshed);
            Assert.Equal("Updated", saved.Channel.Title);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_CancellationDuringPlaylistCallPersistsPriorMetadata()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0);
            repository.AddStaleChannel("channel-2", 1, playlistId: "playlist-2");
            var youtube = new FakeYoutubeService
            {
                BeforePlaylistPageResponse = () => cancellationTokenSource.Cancel()
            };
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated 1",
                Thumbnail = "updated-1.png"
            });
            youtube.SetChannelById("channel-2", new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Updated 2",
                Thumbnail = "updated-2.png",
                PlaylistId = "playlist-2"
            });
            youtube.SetVideos(
                "playlist-2",
                new YoutubeVideo
                {
                    ChannelId = "channel-2",
                    Id = "video-2",
                    Title = "Video 2",
                    Duration = TimeSpan.FromMinutes(2),
                    PublishedAt = DateTimeOffset.UtcNow,
                    Thumbnail = "video-2.png"
                });
            var pipeline = CreatePipeline(repository, youtube);

            var result = await pipeline.RefreshStaleChannelsAsync(cancellationTokenSource.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(0, result.RefreshedChannelCount);
            Assert.Equal(2, repository.SavedResults.Count);
            Assert.Contains(repository.SavedResults, saved => saved.Channel.Id == "channel-1" && saved.Channel.Title == "Updated 1" && !saved.VideosRefreshed);
            Assert.Contains(repository.SavedResults, saved => saved.Channel.Id == "channel-2" && saved.Channel.Title == "Updated 2" && !saved.VideosRefreshed);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_CancellationBetweenDurationChunksPersistsCompletedChannels()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
            repository.AddStaleChannel("channel-2", 1, playlistId: "playlist-2");
            var youtube = new FakeYoutubeService
            {
                BeforeDurationResponse = callCount =>
                {
                    if (callCount == 2)
                    {
                        cancellationTokenSource.Cancel();
                    }
                }
            };
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "One",
                Thumbnail = "one.png",
                PlaylistId = "playlist-1"
            });
            youtube.SetChannelById("channel-2", new YoutubeChannel
            {
                Id = "channel-2",
                Title = "Two",
                Thumbnail = "two.png",
                PlaylistId = "playlist-2"
            });
            youtube.SetVideos("playlist-1", CreateVideos("channel-1", 0, 50, now).ToArray());
            youtube.SetVideos("playlist-2", CreateVideos("channel-2", 50, 1, now).ToArray());
            var pipeline = CreatePipeline(repository, youtube, clock: new FakeAppClock
            {
                UtcNow = now,
                RandomDelayValue = TimeSpan.FromMinutes(60)
            });

            var result = await pipeline.RefreshStaleChannelsAsync(cancellationTokenSource.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(1, result.RefreshedChannelCount);
            Assert.Equal(2, youtube.GetVideoDurationsCallCount);
            var refreshed = Assert.Single(repository.SavedResults, saved => saved.VideosRefreshed);
            Assert.Equal("channel-1", refreshed.Channel.Id);
            Assert.Equal(50, refreshed.Channel.Videos.Count);
            var incomplete = Assert.Single(repository.SavedResults, saved => saved.Channel.Id == "channel-2");
            Assert.False(incomplete.VideosRefreshed);
            Assert.Empty(incomplete.Channel.Videos);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_MetadataMissingWithStoredPlaylistStillRefreshesVideos()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0, playlistId: "playlist-1");
            var youtube = new FakeYoutubeService();
            youtube.SetVideos(
                "playlist-1",
                new YoutubeVideo
                {
                    ChannelId = "channel-1",
                    Id = "video-1",
                    Title = "Video",
                    Duration = TimeSpan.FromMinutes(5),
                    PublishedAt = now,
                    Thumbnail = "video.png"
                });
            var pipeline = CreatePipeline(repository, youtube, clock: new FakeAppClock
            {
                UtcNow = now,
                RandomDelayValue = TimeSpan.FromMinutes(60)
            });

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(1, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(1, youtube.GetVideoDurationsCallCount);
            Assert.Equal(1, result.RefreshedChannelCount);
            Assert.Equal(0, result.UnavailableChannelCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.True(saved.VideosRefreshed);
            Assert.Equal(ChannelStatus.Active, saved.Channel.Status);
            Assert.Equal(ChannelStatusReason.None, saved.Channel.StatusReason);
            var video = Assert.Single(saved.Channel.Videos);
            Assert.Equal("video-1", video.VideoId);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_MetadataMissingWithoutStoredPlaylistMarksUnavailable()
        {
            var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0);
            var youtube = new FakeYoutubeService();
            var pipeline = CreatePipeline(repository, youtube, clock: new FakeAppClock
            {
                UtcNow = now
            });

            var result = await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(0, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(0, youtube.GetVideoDurationsCallCount);
            Assert.Equal(1, result.UnavailableChannelCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.False(saved.VideosRefreshed);
            Assert.Equal(ChannelStatus.Unavailable, saved.Channel.Status);
            Assert.Equal(ChannelStatusReason.NotFound, saved.Channel.StatusReason);
            Assert.Equal(now.Add(Constants.ChannelUnavailableStaleDelay), saved.Channel.StaleAfter);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_CancellationAfterMetadataFallbackPersistsOnlyCompletedResults()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var repository = new FakeChannelRepository();
            repository.AddStaleChannel("channel-1", 0);
            repository.AddStaleChannel("channel-2", 1, playlistId: "playlist-2");
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "updated.png"
            });
            youtube.SetVideos(
                "playlist-2",
                new YoutubeVideo
                {
                    ChannelId = "channel-2",
                    Id = "video-2",
                    Title = "Video 2",
                    Duration = TimeSpan.FromMinutes(2),
                    PublishedAt = DateTimeOffset.UtcNow,
                    Thumbnail = "video-2.png"
                });
            var pipeline = CreatePipeline(
                repository,
                youtube,
                new CancelingYoutubeCallDelay(cancellationTokenSource));

            var result = await pipeline.RefreshStaleChannelsAsync(cancellationTokenSource.Token);

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(0, youtube.GetPlaylistVideosCallCount);
            var saved = Assert.Single(repository.SavedResults);
            Assert.Equal("channel-1", saved.Channel.Id);
            Assert.Equal("Updated", saved.Channel.Title);
            Assert.False(saved.VideosRefreshed);
        }

        [Fact]
        public async Task RefreshStaleChannelsAsync_SavesCanonicalChannelsBeforeProjectionUpdate()
        {
            var events = new List<string>();
            var repository = new FakeChannelRepository(events);
            repository.AddStaleChannel("channel-1", 0);
            var projectionRepository = new RecordingProjectionRepository(events);
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", new YoutubeChannel
            {
                Id = "channel-1",
                Title = "Updated",
                Thumbnail = "updated.png"
            });
            var pipeline = CreatePipeline(
                repository,
                youtube,
                new RecordingYoutubeCallDelay(),
                new FakeAppClock(),
                projectionRepository);

            await pipeline.RefreshStaleChannelsAsync(CancellationToken.None);

            Assert.Equal(new[] { "save", "projection" }, events);
        }

        private static ChannelRefreshPipeline CreatePipeline(
            FakeChannelRepository repository,
            FakeYoutubeService youtube,
            IYoutubeCallDelay delay = null,
            FakeAppClock clock = null,
            IListProjectionRepository projectionRepository = null)
        {
            return new ChannelRefreshPipeline(
                repository,
                youtube,
                projectionRepository ?? new RecordingProjectionRepository(),
                clock ?? new FakeAppClock(),
                delay ?? new RecordingYoutubeCallDelay());
        }

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

        private sealed class FakeChannelRepository : IChannelRepository
        {
            private readonly List<StaleChannelReference> _staleReferences = new List<StaleChannelReference>();
            private readonly Dictionary<string, Channel> _channelsById =
                new Dictionary<string, Channel>(StringComparer.Ordinal);
            private readonly List<string> _events;

            public FakeChannelRepository(List<string> events = null)
            {
                _events = events;
            }

            public IReadOnlyList<string> StaleIds => _staleReferences.Select(channel => channel.Id).ToList();
            public int LastLookaheadTake { get; private set; }
            public IReadOnlyList<string> LastBatchIds { get; private set; } = Array.Empty<string>();
            public IReadOnlyList<ChannelRefreshResult> SavedResults { get; private set; } =
                Array.Empty<ChannelRefreshResult>();

            public void AddStaleChannel(string id, int staleMinutes, string playlistId = null)
            {
                _staleReferences.Add(new StaleChannelReference
                {
                    Id = id,
                    StaleAfter = DateTimeOffset.UtcNow.AddMinutes(staleMinutes)
                });
                _channelsById[id] = new Channel
                {
                    Id = id,
                    Url = string.Format(Constants.YoutubeChannelUrl, id),
                    Title = id,
                    Thumbnail = $"{id}.png",
                    PlaylistId = playlistId,
                    StaleAfter = DateTimeOffset.UtcNow.AddMinutes(staleMinutes),
                    Status = ChannelStatus.Active,
                    StatusReason = ChannelStatusReason.None
                };
            }

            public Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
                DateTimeOffset now,
                int take,
                CancellationToken cancellationToken)
            {
                LastLookaheadTake = take;
                return Task.FromResult<IReadOnlyList<StaleChannelReference>>(
                    _staleReferences
                        .OrderBy(channel => channel.StaleAfter)
                        .ThenBy(channel => channel.Id, StringComparer.Ordinal)
                        .Take(take)
                        .ToList());
            }

            public Task<IReadOnlyList<Channel>> GetBatchAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken)
            {
                LastBatchIds = channelIds.ToList();
                return Task.FromResult<IReadOnlyList<Channel>>(
                    channelIds
                        .Where(_channelsById.ContainsKey)
                        .Select(id => _channelsById[id])
                        .ToList());
            }

            public Task SaveRefreshResultsAsync(
                IReadOnlyCollection<ChannelRefreshResult> results,
                CancellationToken cancellationToken)
            {
                _events?.Add("save");
                SavedResults = results.ToList();
                return Task.CompletedTask;
            }

            public Task<ChannelModel> GetByIdAsync(string id) => throw new NotImplementedException();
            public Task SaveDiscoveredChannelAsync(ChannelModel channel, DateTimeOffset staleAfter) => throw new NotImplementedException();
            public Task UpdateMetadataAsync(string id, string url, string title, string thumbnail, string playlistId) => throw new NotImplementedException();
            public Task MarkUnavailableAsync(string id, ChannelStatusReason reason, DateTimeOffset statusUpdatedAt, DateTimeOffset staleAfter) => throw new NotImplementedException();
            public Task<StaleChannelModel> ClaimNextStaleChannelAsync(DateTimeOffset now, DateTimeOffset visibleAfter) => throw new NotImplementedException();
            public Task<int> RemoveOrphanChannelsAsync(DateTimeOffset now) => throw new NotImplementedException();
        }

        private sealed class RecordingProjectionRepository : IListProjectionRepository
        {
            private readonly List<string> _events;

            public RecordingProjectionRepository(List<string> events = null)
            {
                _events = events;
            }

            public Task UpdateProjectedChannelsAsync(
                IReadOnlyCollection<Channel> refreshedChannels,
                CancellationToken cancellationToken)
            {
                _events?.Add("projection");
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingYoutubeCallDelay : IYoutubeCallDelay
        {
            public int DelayCount { get; private set; }

            public Task DelayAsync(CancellationToken cancellationToken)
            {
                DelayCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class CancelingYoutubeCallDelay : IYoutubeCallDelay
        {
            private readonly CancellationTokenSource _cancellationTokenSource;

            public CancelingYoutubeCallDelay(CancellationTokenSource cancellationTokenSource)
            {
                _cancellationTokenSource = cancellationTokenSource;
            }

            public Task DelayAsync(CancellationToken cancellationToken)
            {
                _cancellationTokenSource.Cancel();
                return Task.FromCanceled(cancellationToken);
            }
        }

        private sealed class CancelingAfterDelayCountYoutubeCallDelay : IYoutubeCallDelay
        {
            private readonly CancellationTokenSource _cancellationTokenSource;
            private readonly int _cancelAfterDelayCount;

            public CancelingAfterDelayCountYoutubeCallDelay(
                CancellationTokenSource cancellationTokenSource,
                int cancelAfterDelayCount)
            {
                _cancellationTokenSource = cancellationTokenSource;
                _cancelAfterDelayCount = cancelAfterDelayCount;
            }

            public int DelayCount { get; private set; }

            public Task DelayAsync(CancellationToken cancellationToken)
            {
                DelayCount++;
                if (DelayCount >= _cancelAfterDelayCount)
                {
                    _cancellationTokenSource.Cancel();
                    return Task.FromCanceled(cancellationToken);
                }

                return Task.CompletedTask;
            }
        }
    }
}
