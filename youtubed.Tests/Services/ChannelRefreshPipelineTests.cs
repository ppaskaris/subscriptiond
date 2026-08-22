using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        private static readonly DateTimeOffset Now =
            new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task RefreshAsync_RejectsCohortLargerThanConfiguredBound()
        {
            var pipeline = CreatePipeline(new RecordingChannelRepository(), new FakeYoutubeService());
            var requests = Enumerable.Range(0, Constants.ChannelRefreshBatchSize + 1)
                .Select(index => Request($"channel-{index}"))
                .ToList();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => pipeline.RefreshAsync(requests, CancellationToken.None));
        }

        [Fact]
        public async Task RefreshAsync_BulkLoadsExplicitIdsAndReconstructsMissingCache()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("existing", playlistId: null));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("existing", Metadata("existing", null));
            youtube.SetChannelById("missing", Metadata("missing", null));

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("missing", ChannelRefreshReason.Missing), Request("existing") },
                CancellationToken.None);

            Assert.Equal(new[] { "missing", "existing" }, repository.LastBatchIds);
            Assert.Equal(1, youtube.GetChannelsByIdCallCount);
            Assert.Equal(2, result.RefreshedChannelCount);
            Assert.Contains(repository.SavedResults, saved => saved.Channel.Id == "missing");
        }

        [Fact]
        public async Task RefreshAsync_NoNewVideosUsesOnePlaylistCallAndNoDurationCall()
        {
            var repository = new RecordingChannelRepository();
            var cached = Video("channel-1", "cached", Now.AddMinutes(-5), TimeSpan.FromMinutes(3));
            repository.Add(Channel("channel-1", "playlist-1", cached));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetVideos("playlist-1", new YoutubeVideo
            {
                ChannelId = "channel-1",
                Id = "cached",
                Title = "Updated title",
                PublishedAt = cached.PublishedAt,
                Thumbnail = "updated.jpg"
            });

            var result = await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1") }, CancellationToken.None);

            var saved = Assert.Single(repository.SavedResults);
            var savedVideo = Assert.Single(saved.Channel.Videos);
            Assert.Equal("Updated title", savedVideo.Title);
            Assert.Equal(cached.Duration, savedVideo.Duration);
            Assert.Equal(1, result.PlaylistCallCount);
            Assert.Equal(0, result.DurationCallCount);
            Assert.Equal(0, youtube.GetVideoDurationsCallCount);
        }

        [Fact]
        public async Task RefreshAsync_InspectsWholeOverlapPageAndDoesNotFetchNextPage()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel(
                "channel-1",
                "playlist-1",
                Video("channel-1", "cached", Now.AddMinutes(-5), TimeSpan.FromMinutes(3))));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetPlaylistPages(
                "playlist-1",
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-2",
                    Videos = new[]
                    {
                        YoutubeVideo("channel-1", "cached", Now.AddMinutes(-5), TimeSpan.FromMinutes(3)),
                        YoutubeVideo("channel-1", "inserted-older", Now.AddDays(-2), TimeSpan.FromMinutes(4))
                    }
                },
                new YoutubePlaylistVideoPage
                {
                    Videos = new[] { YoutubeVideo("channel-1", "not-read", Now.AddDays(-3), TimeSpan.FromMinutes(5)) }
                });

            await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1") }, CancellationToken.None);

            Assert.Equal(1, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(new[] { "inserted-older" }, youtube.LastVideoDurationIds);
            Assert.Contains(
                Assert.Single(repository.SavedResults).Channel.Videos,
                video => video.VideoId == "inserted-older");
        }

        [Fact]
        public async Task RefreshAsync_ColdScanStopsAtRetainedBound()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("channel-1", "playlist-1"));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetPlaylistPages(
                "playlist-1",
                Page("channel-1", 0, 50, "page-2"),
                Page("channel-1", 50, 50, "page-3"),
                Page("channel-1", 100, 20, null));

            await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1", ChannelRefreshReason.Missing) }, CancellationToken.None);

            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(100, Assert.Single(repository.SavedResults).Channel.Videos.Count);
            Assert.All(youtube.VideoDurationRequestIds, ids => Assert.True(ids.Count <= 50));
        }

        [Fact]
        public async Task RefreshAsync_OldPublicationTimeDoesNotStopPlaylistPaging()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel(
                "channel-1",
                "playlist-1",
                Video("channel-1", "cached", Now.AddMinutes(-10), TimeSpan.FromMinutes(1))));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetPlaylistPages(
                "playlist-1",
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-2",
                    Videos = new[]
                    {
                        YoutubeVideo("channel-1", "old-inserted", Now.AddDays(-31), TimeSpan.FromMinutes(1))
                    }
                },
                new YoutubePlaylistVideoPage
                {
                    Videos = new[]
                    {
                        YoutubeVideo("channel-1", "new", Now.AddMinutes(-1), TimeSpan.FromMinutes(2)),
                        YoutubeVideo("channel-1", "cached", Now.AddMinutes(-10), TimeSpan.FromMinutes(1))
                    }
                });

            await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1") }, CancellationToken.None);

            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(new[] { "new" }, youtube.LastVideoDurationIds);
            var videos = Assert.Single(repository.SavedResults).Channel.Videos;
            Assert.Contains(videos, video => video.VideoId == "new");
            Assert.DoesNotContain(videos, video => video.VideoId == "old-inserted");
        }

        [Fact]
        public async Task RefreshAsync_OldItemsDoNotConsumeColdChannelRetainedItemBound()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("channel-1", "playlist-1"));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetPlaylistPages(
                "playlist-1",
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-2",
                    Videos = Enumerable.Range(0, 50)
                        .Select(index => YoutubeVideo(
                            "channel-1",
                            $"old-{index:D3}",
                            Now.AddDays(-31),
                            TimeSpan.FromMinutes(1)))
                        .ToList()
                },
                new YoutubePlaylistVideoPage
                {
                    NextPageToken = "page-3",
                    Videos = Enumerable.Range(50, 50)
                        .Select(index => YoutubeVideo(
                            "channel-1",
                            $"old-{index:D3}",
                            Now.AddDays(-31),
                            TimeSpan.FromMinutes(1)))
                        .ToList()
                },
                new YoutubePlaylistVideoPage
                {
                    Videos = new[]
                    {
                        YoutubeVideo("channel-1", "retained", Now.AddMinutes(-1), TimeSpan.FromMinutes(2))
                    }
                });

            await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1", ChannelRefreshReason.Missing) }, CancellationToken.None);

            Assert.Equal(3, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(new[] { "retained" }, youtube.LastVideoDurationIds);
            Assert.Equal("retained", Assert.Single(
                Assert.Single(repository.SavedResults).Channel.Videos).VideoId);
        }

        [Fact]
        public async Task RefreshAsync_SharesDurationChunksAcrossChannelsAndFetchesOnlyNewIds()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetVideos("playlist-1", Videos("channel-1", 0, 30).ToArray());
            youtube.SetVideos("playlist-2", Videos("channel-2", 30, 30).ToArray());

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);

            Assert.Equal(1, result.MetadataCallCount);
            Assert.Equal(2, result.DurationCallCount);
            Assert.Equal(new[] { 50, 10 }, youtube.VideoDurationRequestIds.Select(ids => ids.Count));
            Assert.Equal(2, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshAsync_TransientPlaylistFailureStopsCohortAndPreservesCompletedPeer()
        {
            var repository = new RecordingChannelRepository();
            var cached = Video("channel-1", "cached", Now.AddMinutes(-1), TimeSpan.FromMinutes(1));
            repository.Add(Channel("channel-1", "playlist-1", cached));
            repository.Add(Channel("channel-2", "playlist-2"));
            repository.Add(Channel("channel-3", "playlist-3"));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-3", Metadata("channel-3", "playlist-3"));
            youtube.SetVideos("playlist-1", YoutubeVideo("channel-1", "cached", cached.PublishedAt, cached.Duration));
            var call = 0;
            youtube.BeforePlaylistPageResponse = () =>
            {
                if (++call == 2)
                {
                    throw new YoutubeTransientException("expected", new InvalidOperationException());
                }
            };

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2"), Request("channel-3") },
                CancellationToken.None);

            Assert.Equal(ChannelRefreshDisposition.Refreshed,
                result.Outcomes.Single(outcome => outcome.ChannelId == "channel-1").Disposition);
            Assert.All(result.Outcomes.Where(outcome => outcome.ChannelId != "channel-1"), outcome =>
                Assert.Equal(ChannelRefreshDisposition.RetryTransient, outcome.Disposition));
            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal("channel-1", Assert.Single(repository.SavedResults).Channel.Id);
        }

        [Fact]
        public async Task RefreshAsync_PlaylistQuotaFailureStartsNoLaterYoutubeCalls()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            repository.Add(Channel("channel-3", "playlist-3"));
            var youtube = new FakeYoutubeService();
            foreach (var id in new[] { "channel-1", "channel-2", "channel-3" })
            {
                youtube.SetChannelById(id, Metadata(id, id.Replace("channel", "playlist")));
            }
            var call = 0;
            youtube.BeforePlaylistPageResponse = () =>
            {
                if (++call == 2)
                {
                    throw new YoutubeQuotaExceededException(Now.AddHours(1), new InvalidOperationException());
                }
            };

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2"), Request("channel-3") },
                CancellationToken.None);

            Assert.Equal(2, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(new[] { "channel-2", "channel-3" }, result.RetryChannelIds);
            Assert.Equal("channel-1", Assert.Single(repository.SavedResults).Channel.Id);
        }

        [Fact]
        public async Task RefreshAsync_MissingMetadataIsNegativeCached()
        {
            var repository = new RecordingChannelRepository();

            var result = await CreatePipeline(repository, new FakeYoutubeService()).RefreshAsync(
                new[] { Request("gone", ChannelRefreshReason.Missing) },
                CancellationToken.None);

            var saved = Assert.Single(repository.SavedResults).Channel;
            Assert.Equal("gone", saved.Id);
            Assert.Equal(ChannelStatus.Unavailable, saved.Status);
            Assert.Equal(ChannelStatusReason.NotFound, saved.StatusReason);
            Assert.Equal(ChannelRefreshDisposition.Unavailable, Assert.Single(result.Outcomes).Disposition);
        }

        [Fact]
        public async Task RefreshAsync_PersistenceFailureRetriesOnlyFailedChannel()
        {
            var repository = new RecordingChannelRepository { FailSaveForId = "channel-1" };
            repository.Add(Channel("channel-1", null));
            repository.Add(Channel("channel-2", null));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", null));
            youtube.SetChannelById("channel-2", Metadata("channel-2", null));

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);

            Assert.Equal(new[] { "channel-1" }, result.RetryChannelIds);
            Assert.Equal("channel-2", Assert.Single(repository.SavedResults).Channel.Id);
        }

        [Fact]
        public async Task RefreshAsync_QuotaExhaustionRequeuesWithoutMarkingUnavailable()
        {
            var repository = new RecordingChannelRepository();
            repository.Add(Channel("channel-1", "playlist-1"));
            var youtube = new FakeYoutubeService
            {
                ChannelsByIdException = new YoutubeQuotaExceededException(
                    Now.AddHours(1),
                    new InvalidOperationException("expected"))
            };

            var result = await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1") }, CancellationToken.None);

            Assert.Equal(new[] { "channel-1" }, result.RetryChannelIds);
            Assert.Empty(repository.SavedResults);
            Assert.Equal(0, result.UnavailableChannelCount);
        }

        [Fact]
        public async Task RefreshAsync_PrunesAndBoundsVideosDeterministically()
        {
            var repository = new RecordingChannelRepository();
            var cached = Enumerable.Range(0, 105)
                .Select(index => Video(
                    "channel-1",
                    $"video-{index:D3}",
                    index == 104 ? Now.AddDays(-31) : Now.AddMinutes(-(index % 4)),
                    TimeSpan.FromMinutes(1)))
                .ToArray();
            repository.Add(Channel("channel-1", "playlist-1", cached));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetVideos("playlist-1", YoutubeVideo("channel-1", "video-000", Now, TimeSpan.FromMinutes(1)));

            await CreatePipeline(repository, youtube)
                .RefreshAsync(new[] { Request("channel-1") }, CancellationToken.None);

            var videos = Assert.Single(repository.SavedResults).Channel.Videos;
            Assert.Equal(100, videos.Count);
            Assert.DoesNotContain(videos, video => video.VideoId == "video-104");
            Assert.Equal(
                videos.OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .Select(video => video.VideoId),
                videos.Select(video => video.VideoId));
        }

        private static ChannelRefreshPipeline CreatePipeline(
            RecordingChannelRepository repository,
            FakeYoutubeService youtube,
            YoutubeSyncOptions options = null)
        {
            return new ChannelRefreshPipeline(
                repository,
                youtube,
                new FakeAppClock { UtcNow = Now, RandomDelayValue = TimeSpan.FromMinutes(60) },
                Options.Create(options ?? new YoutubeSyncOptions()),
                NullLogger<ChannelRefreshPipeline>.Instance);
        }

        private static ChannelRefreshRequest Request(
            string id,
            ChannelRefreshReason reason = ChannelRefreshReason.Stale) => new(id, reason, Now.AddHours(-1));

        private static Channel Channel(string id, string playlistId, params ChannelVideo[] videos) => new()
        {
            Id = id,
            Url = string.Format(Constants.YoutubeChannelUrl, id),
            Title = id,
            Thumbnail = $"{id}.jpg",
            PlaylistId = playlistId,
            StaleAfter = Now.AddHours(-1),
            Videos = videos
        };

        private static YoutubeChannel Metadata(string id, string playlistId) => new()
        {
            Id = id,
            Title = $"Updated {id}",
            Thumbnail = $"{id}-updated.jpg",
            PlaylistId = playlistId
        };

        private static ChannelVideo Video(
            string channelId,
            string id,
            DateTimeOffset publishedAt,
            TimeSpan duration) => new()
        {
            ChannelId = channelId,
            VideoId = id,
            Title = id,
            Duration = duration,
            PublishedAt = publishedAt,
            ThumbnailUrl = $"{id}.jpg"
        };

        private static YoutubeVideo YoutubeVideo(
            string channelId,
            string id,
            DateTimeOffset publishedAt,
            TimeSpan duration) => new()
        {
            ChannelId = channelId,
            Id = id,
            Title = id,
            Duration = duration,
            PublishedAt = publishedAt,
            Thumbnail = $"{id}.jpg"
        };

        private static IEnumerable<YoutubeVideo> Videos(string channelId, int start, int count) =>
            Enumerable.Range(start, count)
                .Select(index => YoutubeVideo(
                    channelId,
                    $"video-{index:D3}",
                    Now.AddMinutes(-index),
                    TimeSpan.FromMinutes(1)));

        private static YoutubePlaylistVideoPage Page(
            string channelId,
            int start,
            int count,
            string nextPageToken) => new()
        {
            NextPageToken = nextPageToken,
            Videos = Videos(channelId, start, count).ToList()
        };

        private sealed class RecordingChannelRepository : IChannelRepository
        {
            private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

            public IReadOnlyList<string> LastBatchIds { get; private set; } = Array.Empty<string>();
            public List<ChannelRefreshResult> SavedResults { get; } = new();
            public string FailSaveForId { get; set; }

            public void Add(Channel channel) => _channels[channel.Id] = channel;

            public Task<IReadOnlyList<Channel>> GetBatchAsync(
                IReadOnlyCollection<string> channelIds,
                CancellationToken cancellationToken)
            {
                LastBatchIds = channelIds.ToList();
                return Task.FromResult<IReadOnlyList<Channel>>(channelIds
                    .Where(_channels.ContainsKey)
                    .Select(id => _channels[id])
                    .ToList());
            }

            public Task SaveRefreshResultAsync(
                ChannelRefreshResult result,
                CancellationToken cancellationToken)
            {
                if (result.Channel.Id == FailSaveForId)
                {
                    throw new InvalidOperationException("Expected persistence failure.");
                }

                SavedResults.Add(result);
                _channels[result.Channel.Id] = result.Channel;
                return Task.CompletedTask;
            }

            public Task<Channel> GetByIdAsync(string id) => throw new NotImplementedException();
            public Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter) =>
                throw new NotImplementedException();
        }
    }
}
