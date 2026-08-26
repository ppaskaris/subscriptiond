using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
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
        private readonly ITestOutputHelper _output;

        public ChannelRefreshPipelineTests(ITestOutputHelper output)
        {
            _output = output;
        }

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
            Assert.Equal(
                new[] { 50, 10 },
                youtube.VideoDurationRequestIds.Select(ids => ids.Count).OrderByDescending(count => count));
            Assert.Equal(2, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshAsync_TransientPlaylistFailureStopsNewWorkAndPreservesCompletedPeers()
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

            Assert.Single(result.Outcomes, outcome =>
                outcome.Disposition == ChannelRefreshDisposition.RetryTransient);
            Assert.Equal(2, result.Outcomes.Count(outcome =>
                outcome.Disposition == ChannelRefreshDisposition.Refreshed));
            Assert.False(result.CanceledDuringYoutubeWork);
            Assert.Equal(3, youtube.GetPlaylistVideosCallCount);
            Assert.Equal(2, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshAsync_PlaylistQuotaFailurePreservesCompletedWork()
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

            Assert.InRange(youtube.GetPlaylistVideosCallCount, 2, 3);
            Assert.Equal(3, result.Outcomes.Count);
            Assert.InRange(result.RetryChannelIds.Count, 1, 2);
            Assert.Equal(result.RefreshedChannelCount, repository.SavedResults.Count);
            Assert.True(result.RefreshedChannelCount >= 1);
        }

        [Fact]
        public async Task RefreshAsync_MetadataPrecedesFourConcurrentPlaylistPlansAndPagesStaySequential()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            foreach (var index in Enumerable.Range(1, 6))
            {
                var channelId = $"channel-{index}";
                var playlistId = $"playlist-{index}";
                repository.Add(Channel(channelId, playlistId));
                youtube.SetChannelById(channelId, Metadata(channelId, playlistId));
                youtube.SetPlaylistPages(
                    playlistId,
                    new YoutubePlaylistVideoPage
                    {
                        NextPageToken = "page-2",
                        Videos = Array.Empty<YoutubeVideo>()
                    },
                    new YoutubePlaylistVideoPage());
            }

            var fourEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var current = 0;
            var maximum = 0;
            var activePlaylists = new HashSet<string>(StringComparer.Ordinal);
            var sync = new object();
            youtube.BeforePlaylistPageResponseAsync = async (playlistId, pageToken, token) =>
            {
                Assert.Equal(1, youtube.GetChannelsByIdCallCount);
                lock (sync)
                {
                    Assert.True(activePlaylists.Add(playlistId), "Pages for one playlist overlapped.");
                }
                var entered = Interlocked.Increment(ref current);
                UpdateMaximum(ref maximum, entered);
                if (entered == 4)
                {
                    fourEntered.TrySetResult();
                }
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref current);
                lock (sync)
                {
                    activePlaylists.Remove(playlistId);
                }
            };

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                Enumerable.Range(1, 6).Select(index => Request($"channel-{index}")).ToList(),
                CancellationToken.None);
            await fourEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, Volatile.Read(ref current));
            release.SetResult();

            var result = await refresh;
            Assert.Equal(1, result.MetadataCallCount);
            Assert.Equal(12, result.PlaylistCallCount);
            Assert.Equal(4, maximum);
            Assert.Equal(6, result.Outcomes.Count);
            Assert.All(result.Outcomes, outcome =>
                Assert.Equal(ChannelRefreshDisposition.Refreshed, outcome.Disposition));
        }

        [Fact]
        public async Task RefreshAsync_PersistsCompletedPlansAfterPlaylistStageBarrier()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            foreach (var index in Enumerable.Range(1, 2))
            {
                var channelId = $"channel-{index}";
                var playlistId = $"playlist-{index}";
                var cached = Video(channelId, $"cached-{index}", Now.AddMinutes(-1), TimeSpan.FromMinutes(1));
                repository.Add(Channel(channelId, playlistId, cached));
                youtube.SetChannelById(channelId, Metadata(channelId, playlistId));
                youtube.SetVideos(
                    playlistId,
                    YoutubeVideo(channelId, cached.VideoId, cached.PublishedAt, cached.Duration));
            }
            var releaseSlow = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforePlaylistPageResponseAsync = (playlistId, pageToken, token) =>
                playlistId == "playlist-1"
                    ? releaseSlow.Task.WaitAsync(token)
                    : Task.CompletedTask;

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            Assert.False(refresh.IsCompleted);
            Assert.Empty(repository.SavedResults);
            releaseSlow.SetResult();
            Assert.Equal(2, (await refresh).RefreshedChannelCount);
            Assert.Equal(2, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshAsync_PersistsPlaylistCompletePeerBeforeDurationStageSettles()
        {
            var repository = new RecordingChannelRepository();
            var cached = Video(
                "channel-1",
                "cached-1",
                Now.AddMinutes(-1),
                TimeSpan.FromMinutes(1));
            repository.Add(Channel("channel-1", "playlist-1", cached));
            repository.Add(Channel("channel-2", "playlist-2"));
            var youtube = new FakeYoutubeService();
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetVideos(
                "playlist-1",
                YoutubeVideo("channel-1", cached.VideoId, cached.PublishedAt, cached.Duration));
            youtube.SetVideos(
                "playlist-2",
                YoutubeVideo("channel-2", "new-2", Now, TimeSpan.FromMinutes(2)));
            var durationEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDuration = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforeDurationResponseAsync = async (ids, token) =>
            {
                durationEntered.SetResult();
                await releaseDuration.Task.WaitAsync(token);
            };

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);
            await durationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(refresh.IsCompleted);
            Assert.Equal("channel-1", Assert.Single(repository.SavedResults).Channel.Id);
            releaseDuration.SetResult();
            Assert.Equal(2, (await refresh).RefreshedChannelCount);
        }

        [Fact]
        public async Task RefreshAsync_PublishesCompletedBatchGlobalStopBeforeAwaitingEarlierSave()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            foreach (var index in Enumerable.Range(1, 3))
            {
                var channelId = $"channel-{index}";
                var playlistId = $"playlist-{index}";
                var cached = Video(channelId, $"cached-{index}", Now.AddMinutes(-1), TimeSpan.FromMinutes(1));
                repository.Add(Channel(channelId, playlistId, cached));
                youtube.SetChannelById(channelId, Metadata(channelId, playlistId));
                youtube.SetVideos(
                    playlistId,
                    YoutubeVideo(channelId, cached.VideoId, cached.PublishedAt, cached.Duration));
            }
            var siblingCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforePlaylistPageResponseAsync = async (playlistId, pageToken, token) =>
            {
                if (playlistId == "playlist-2")
                {
                    throw new YoutubeTransientException("expected", null);
                }
                if (playlistId == "playlist-3")
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        siblingCanceled.TrySetResult();
                        throw;
                    }
                }
            };
            var saveEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSave = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            repository.BeforeSaveAsync = async (saved, token) =>
            {
                if (saved.Channel.Id == "channel-1")
                {
                    saveEntered.TrySetResult();
                    await releaseSave.Task;
                }
            };

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2"), Request("channel-3") },
                CancellationToken.None);
            await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await siblingCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted);

            releaseSave.SetResult();
            var result = await refresh;
            Assert.Equal(ChannelRefreshDisposition.Refreshed, result.Outcomes[0].Disposition);
            Assert.All(result.Outcomes.Skip(1), outcome =>
                Assert.Equal(ChannelRefreshDisposition.RetryTransient, outcome.Disposition));
        }

        [Fact]
        public async Task RefreshAsync_PermanentPlaylistFailureIsIsolated()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            foreach (var index in Enumerable.Range(1, 3))
            {
                var channelId = $"channel-{index}";
                var playlistId = $"playlist-{index}";
                repository.Add(Channel(channelId, playlistId));
                youtube.SetChannelById(channelId, Metadata(channelId, playlistId));
                youtube.SetVideos(playlistId);
            }
            youtube.BeforePlaylistPageResponseAsync = (playlistId, pageToken, token) =>
                playlistId == "playlist-2"
                    ? Task.FromException(new YoutubePermanentException("expected", null))
                    : Task.CompletedTask;

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                Enumerable.Range(1, 3).Select(index => Request($"channel-{index}")).ToList(),
                CancellationToken.None);

            Assert.Equal(ChannelRefreshDisposition.FailedPermanent,
                result.Outcomes.Single(outcome => outcome.ChannelId == "channel-2").Disposition);
            Assert.All(result.Outcomes.Where(outcome => outcome.ChannelId != "channel-2"), outcome =>
                Assert.Equal(ChannelRefreshDisposition.Refreshed, outcome.Disposition));
        }

        [Fact]
        public async Task RefreshAsync_DurationChunksRunConcurrentlyAndAggregateOutOfOrder()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetVideos("playlist-1", Videos("channel-1", 0, 50).ToArray());
            youtube.SetVideos("playlist-2", Videos("channel-2", 50, 50).ToArray());
            var twoEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            youtube.BeforeDurationResponseAsync = async (ids, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    twoEntered.TrySetResult();
                }
                await release.Task.WaitAsync(token);
            };

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);
            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            var result = await refresh;

            Assert.Equal(2, result.DurationCallCount);
            Assert.All(result.Outcomes, outcome => Assert.Equal(1, outcome.DurationCalls));
            Assert.Equal(2, repository.SavedResults.Count);
        }

        [Fact]
        public async Task RefreshAsync_ConcurrentPermanentAndTransientDurationFailuresUsePermanentPrecedence()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            var firstVideos = Enumerable.Range(0, 60)
                .Select(index => YoutubeVideo(
                    "channel-1",
                    $"shared-{index:D3}",
                    Now.AddMinutes(-index),
                    TimeSpan.FromMinutes(1)))
                .ToArray();
            var secondVideos = firstVideos.Skip(50)
                .Select(video => YoutubeVideo(
                    "channel-2",
                    video.Id,
                    video.PublishedAt,
                    video.Duration))
                .ToArray();
            youtube.SetVideos("playlist-1", firstVideos);
            youtube.SetVideos("playlist-2", secondVideos);
            var bothEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            youtube.BeforeDurationResponseAsync = async (ids, token) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.TrySetResult();
                }
                await bothEntered.Task;
                if (ids.Contains("shared-000"))
                {
                    throw new YoutubePermanentException("expected", null);
                }
                throw new YoutubeTransientException("expected", null);
            };

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);

            Assert.Equal(ChannelRefreshDisposition.FailedPermanent,
                result.Outcomes.Single(outcome => outcome.ChannelId == "channel-1").Disposition);
            Assert.Equal(ChannelRefreshDisposition.RetryTransient,
                result.Outcomes.Single(outcome => outcome.ChannelId == "channel-2").Disposition);
            Assert.False(result.CanceledDuringYoutubeWork);
            Assert.Equal(0, result.DurationCallCount);
            Assert.Equal(2, youtube.GetVideoDurationsCallCount);
        }

        [Fact]
        public async Task RefreshAsync_ExternalCancellationIsDistinctFromInternalGlobalStop()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforePlaylistPageResponseAsync = async (playlistId, pageToken, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            using var cancellation = new CancellationTokenSource();
            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                cancellation.Token);
            await entered.Task;
            cancellation.Cancel();

            var result = await refresh;
            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(2, result.Outcomes.Count);
            Assert.All(result.Outcomes, outcome =>
                Assert.Equal(ChannelRefreshDisposition.RetryTransient, outcome.Disposition));
        }

        [Fact]
        public async Task RefreshAsync_OutOfOrderPlaylistCompletionKeepsExactOrderedOutcomesAndCounts()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            var releases = Enumerable.Range(1, 3).ToDictionary(
                index => $"playlist-{index}",
                index => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                StringComparer.Ordinal);
            var saves = Enumerable.Range(1, 3).ToDictionary(
                index => $"channel-{index}",
                index => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                StringComparer.Ordinal);
            var savedOrder = new List<string>();
            repository.OnSaved = saved =>
            {
                savedOrder.Add(saved.Channel.Id);
                saves[saved.Channel.Id].TrySetResult();
            };
            var threeEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            youtube.BeforePlaylistPageResponseAsync = async (playlistId, pageToken, token) =>
            {
                if (Interlocked.Increment(ref entered) == 3)
                {
                    threeEntered.TrySetResult();
                }
                await releases[playlistId].Task.WaitAsync(token);
            };
            foreach (var index in Enumerable.Range(1, 3))
            {
                var channelId = $"channel-{index}";
                var playlistId = $"playlist-{index}";
                var cached = Video(channelId, $"cached-{index}", Now.AddMinutes(-1), TimeSpan.FromMinutes(1));
                repository.Add(Channel(channelId, playlistId, cached));
                youtube.SetChannelById(channelId, Metadata(channelId, playlistId));
                youtube.SetVideos(
                    playlistId,
                    YoutubeVideo(channelId, cached.VideoId, cached.PublishedAt, cached.Duration));
            }

            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                Enumerable.Range(1, 3).Select(index => Request($"channel-{index}")).ToList(),
                CancellationToken.None);
            await threeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var index in new[] { 3, 1, 2 })
            {
                releases[$"playlist-{index}"].SetResult();
            }

            var result = await refresh;
            Assert.Equal(new[] { "channel-1", "channel-2", "channel-3" },
                result.Outcomes.Select(outcome => outcome.ChannelId));
            Assert.Equal(new[] { "channel-1", "channel-2", "channel-3" }, savedOrder);
            Assert.Equal(3, result.PlaylistCallCount);
            Assert.Equal(0, result.DurationCallCount);
            Assert.All(result.Outcomes, outcome =>
            {
                Assert.Equal(ChannelRefreshDisposition.Refreshed, outcome.Disposition);
                Assert.Equal(1, outcome.PlaylistCalls);
                Assert.Equal(0, outcome.DurationCalls);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RefreshAsync_DurationFailureAffectsOnlyExactDependentPlans(bool permanent)
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetVideos("playlist-1", Videos("channel-1", 0, 50).ToArray());
            youtube.SetVideos("playlist-2", Videos("channel-2", 50, 50).ToArray());
            youtube.BeforeDurationResponseAsync = (ids, token) =>
                ids.Contains("video-000")
                    ? Task.FromException(permanent
                        ? new YoutubePermanentException("expected", null)
                        : new YoutubeTransientException("expected", null))
                    : Task.CompletedTask;

            var result = await CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                CancellationToken.None);

            Assert.Equal(
                permanent
                    ? ChannelRefreshDisposition.FailedPermanent
                    : ChannelRefreshDisposition.RetryTransient,
                result.Outcomes.Single(outcome => outcome.ChannelId == "channel-1").Disposition);
            var independent = result.Outcomes.Single(outcome => outcome.ChannelId == "channel-2");
            Assert.Equal(ChannelRefreshDisposition.Refreshed, independent.Disposition);
            Assert.Equal(1, independent.DurationCalls);
            Assert.Equal(1, result.DurationCallCount);
            Assert.Equal("channel-2", Assert.Single(repository.SavedResults).Channel.Id);
        }

        [Fact]
        public async Task RefreshAsync_PermanentDurationResultBeatsCanceledChunkDuringShutdownRace()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetVideos("playlist-1", Videos("channel-1", 0, 60).ToArray());
            var secondChunkEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforeDurationResponseAsync = async (ids, token) =>
            {
                if (ids.Contains("video-000"))
                {
                    throw new YoutubePermanentException("expected", null);
                }
                secondChunkEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            using var cancellation = new CancellationTokenSource();
            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1") },
                cancellation.Token);
            await secondChunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            var result = await refresh;
            var outcome = Assert.Single(result.Outcomes);
            Assert.Equal("channel-1", outcome.ChannelId);
            Assert.Equal(ChannelRefreshDisposition.FailedPermanent, outcome.Disposition);
            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(0, result.DurationCallCount);
            Assert.Equal(2, youtube.GetVideoDurationsCallCount);
            Assert.Empty(repository.SavedResults);
        }

        [Fact]
        public async Task RefreshAsync_CancellationDuringDurationWorkPersistsCompletedPeer()
        {
            var repository = new RecordingChannelRepository();
            var youtube = new FakeYoutubeService();
            repository.Add(Channel("channel-1", "playlist-1"));
            repository.Add(Channel("channel-2", "playlist-2"));
            youtube.SetChannelById("channel-1", Metadata("channel-1", "playlist-1"));
            youtube.SetChannelById("channel-2", Metadata("channel-2", "playlist-2"));
            youtube.SetVideos("playlist-1", Videos("channel-1", 0, 50).ToArray());
            youtube.SetVideos("playlist-2", Videos("channel-2", 50, 50).ToArray());
            var completedPeerReturned = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var canceledPeerEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            youtube.BeforeDurationResponseAsync = async (ids, token) =>
            {
                if (ids.Contains("video-000"))
                {
                    completedPeerReturned.TrySetResult();
                    return;
                }
                canceledPeerEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            };
            using var cancellation = new CancellationTokenSource();
            var refresh = CreatePipeline(repository, youtube).RefreshAsync(
                new[] { Request("channel-1"), Request("channel-2") },
                cancellation.Token);
            await Task.WhenAll(completedPeerReturned.Task, canceledPeerEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(50));

            cancellation.Cancel();
            var result = await refresh;

            Assert.True(result.CanceledDuringYoutubeWork);
            Assert.Equal(ChannelRefreshDisposition.Refreshed, result.Outcomes[0].Disposition);
            Assert.Equal(ChannelRefreshDisposition.RetryTransient, result.Outcomes[1].Disposition);
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
            IYoutubeService youtube,
            YoutubeSyncOptions options = null)
        {
            var clock = new FakeAppClock
            {
                UtcNow = Now,
                RandomDelayValue = TimeSpan.FromMinutes(60)
            };
            var configured = Options.Create(options ?? new YoutubeSyncOptions());
            return new ChannelRefreshPipeline(
                repository,
                youtube,
                clock,
                configured,
                new YoutubePlaylistScanner(
                    youtube,
                    clock,
                    configured,
                    NullLogger<YoutubePlaylistScanner>.Instance),
                new YoutubeDurationFetcher(
                    youtube,
                    configured,
                    NullLogger<YoutubeDurationFetcher>.Instance),
                new ChannelRefreshMerger(clock, configured),
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

        private static void UpdateMaximum(ref int target, int value)
        {
            var observed = Volatile.Read(ref target);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref target, value, observed);
                if (previous == observed)
                {
                    return;
                }
                observed = previous;
            }
        }

        private static async Task DrainContinuationsAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1));
            for (var index = 0; index < 4; index++)
            {
                await Task.Yield();
            }
        }

        private sealed class RecordingChannelRepository : IChannelRepository
        {
            private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);

            public IReadOnlyList<string> LastBatchIds { get; private set; } = Array.Empty<string>();
            public List<ChannelRefreshResult> SavedResults { get; } = new();
            public string FailSaveForId { get; set; }
            public Action<ChannelRefreshResult> OnSaved { get; set; }
            public Func<ChannelRefreshResult, CancellationToken, Task> BeforeSaveAsync { get; set; }

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

            public async Task SaveRefreshResultAsync(
                ChannelRefreshResult result,
                CancellationToken cancellationToken)
            {
                if (result.Channel.Id == FailSaveForId)
                {
                    throw new InvalidOperationException("Expected persistence failure.");
                }

                if (BeforeSaveAsync != null)
                {
                    await BeforeSaveAsync(result, cancellationToken);
                }
                SavedResults.Add(result);
                _channels[result.Channel.Id] = result.Channel;
                OnSaved?.Invoke(result);
            }

            public Task<Channel> GetByIdAsync(string id) => throw new NotImplementedException();
            public Task SaveDiscoveredChannelAsync(Channel channel, DateTimeOffset staleAfter) =>
                throw new NotImplementedException();
        }
    }
}
