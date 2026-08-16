using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Persistence;

namespace youtubed.Services
{
    public sealed class ChannelRefreshPipeline : IChannelRefreshPipeline
    {
        private sealed class PlaylistPlan
        {
            public ChannelRefreshRequest Request { get; init; }
            public Channel Channel { get; init; }
            public IReadOnlyList<ChannelVideo> CachedVideos { get; init; }
            public IReadOnlyList<YoutubeVideo> ScannedVideos { get; set; } = Array.Empty<YoutubeVideo>();
            public IReadOnlyList<string> NewVideoIds { get; set; } = Array.Empty<string>();
            public int PlaylistCalls { get; set; }
            public int DurationCalls { get; set; }
        }

        private readonly IChannelRepository _channelRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;
        private readonly YoutubeSyncOptions _options;
        private readonly ILogger<ChannelRefreshPipeline> _logger;

        public ChannelRefreshPipeline(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService,
            IAppClock clock,
            IOptions<YoutubeSyncOptions> options,
            ILogger<ChannelRefreshPipeline> logger)
        {
            _channelRepository = channelRepository;
            _youtubeService = youtubeService;
            _clock = clock;
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (_options.CohortSize <= 0
                || _options.CohortSize > 50
                || _options.MaximumPlaylistPages <= 0
                || _options.MaximumVideosPerChannel <= 0
                || _options.MaximumVideosPerChannel > Constants.ListRenderMaxItems)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "YouTube sync cohorts must contain 1-50 channels and retained videos must remain within the document bound.");
            }
        }

        public async Task<ChannelRefreshPipelineResult> RefreshAsync(
            IReadOnlyCollection<ChannelRefreshRequest> requests,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requests);
            if (requests.Count > _options.CohortSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requests),
                    $"A refresh cohort cannot exceed {_options.CohortSize} channels.");
            }

            var selected = requests
                .Where(request => request != null && !string.IsNullOrWhiteSpace(request.ChannelId))
                .GroupBy(request => request.ChannelId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(request => request.Reason).First())
                .ToList();
            var result = new ChannelRefreshPipelineResult
            {
                SelectedChannelCount = selected.Count
            };
            var outcomes = new Dictionary<string, ChannelRefreshOutcome>(StringComparer.Ordinal);
            if (selected.Count == 0)
            {
                return result;
            }

            var cachedChannels = await _channelRepository.GetBatchAsync(
                selected.Select(request => request.ChannelId).ToList(),
                cancellationToken);
            var cachedById = cachedChannels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);

            if (cancellationToken.IsCancellationRequested)
            {
                result.CanceledBeforeStartingYoutubeCall = true;
                AddRetryOutcomes(selected, outcomes);
                return CompleteResult(result, outcomes);
            }

            IReadOnlyDictionary<string, YoutubeChannel> metadataById;
            try
            {
                metadataById = await _youtubeService.GetChannelsByIdAsync(
                    selected.Select(request => request.ChannelId).ToList(),
                    cancellationToken);
                result.MetadataCallCount = 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.CanceledDuringYoutubeWork = true;
                AddRetryOutcomes(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (YoutubePermanentException)
            {
                AddPermanentOutcomes(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                AddRetryOutcomes(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "YouTube metadata request failed permanently.");
                AddPermanentOutcomes(selected, outcomes);
                return CompleteResult(result, outcomes);
            }

            var playlistPlans = new List<PlaylistPlan>();
            foreach (var request in selected)
            {
                cachedById.TryGetValue(request.ChannelId, out var cached);
                if (!metadataById.TryGetValue(request.ChannelId, out var metadata))
                {
                    var unavailable = cached ?? CreateMissingChannel(request.ChannelId);
                    MarkUnavailable(unavailable);
                    await SaveOutcomeAsync(
                        request,
                        new ChannelRefreshResult { Channel = unavailable, VideosRefreshed = false },
                        ChannelRefreshDisposition.Unavailable,
                        playlistCalls: 0,
                        durationCalls: 0,
                        outcomes);
                    continue;
                }

                var channel = cached ?? CreateMissingChannel(request.ChannelId);
                var cachedVideos = channel.Videos?.ToList() ?? new List<ChannelVideo>();
                ApplyMetadata(channel, metadata);
                if (string.IsNullOrWhiteSpace(channel.PlaylistId))
                {
                    SetNextRefresh(channel);
                    await SaveOutcomeAsync(
                        request,
                        new ChannelRefreshResult { Channel = channel, VideosRefreshed = false },
                        ChannelRefreshDisposition.Refreshed,
                        playlistCalls: 0,
                        durationCalls: 0,
                        outcomes);
                    continue;
                }

                playlistPlans.Add(new PlaylistPlan
                {
                    Request = request,
                    Channel = channel,
                    CachedVideos = cachedVideos
                });
            }

            var readyPlans = new List<PlaylistPlan>();
            var durationsById = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            var youtubeWorkHalted = false;
            foreach (var plan in playlistPlans)
            {
                if (youtubeWorkHalted || cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork |= cancellationToken.IsCancellationRequested;
                    outcomes[plan.Request.ChannelId] = Retry(plan.Request.ChannelId, plan.PlaylistCalls, 0);
                    continue;
                }

                try
                {
                    await FetchPlaylistIncrementallyAsync(plan, cancellationToken);
                    if (plan.NewVideoIds.Count == 0)
                    {
                        await MergeAndSaveAsync(plan, durationsById, outcomes);
                    }
                    else
                    {
                        readyPlans.Add(plan);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork = true;
                    youtubeWorkHalted = true;
                    outcomes[plan.Request.ChannelId] = Retry(plan.Request.ChannelId, plan.PlaylistCalls, 0);
                }
                catch (YoutubePermanentException)
                {
                    outcomes[plan.Request.ChannelId] = new ChannelRefreshOutcome(
                        plan.Request.ChannelId,
                        ChannelRefreshDisposition.FailedPermanent,
                        plan.PlaylistCalls,
                        0);
                }
                catch (Exception exception) when (IsTransient(exception))
                {
                    youtubeWorkHalted = true;
                    outcomes[plan.Request.ChannelId] = Retry(plan.Request.ChannelId, plan.PlaylistCalls, 0);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "YouTube playlist request failed permanently. ChannelId={ChannelId}.",
                        plan.Request.ChannelId);
                    outcomes[plan.Request.ChannelId] = new ChannelRefreshOutcome(
                        plan.Request.ChannelId,
                        ChannelRefreshDisposition.FailedPermanent,
                        plan.PlaylistCalls,
                        0);
                }
                finally
                {
                    result.PlaylistCallCount += plan.PlaylistCalls;
                }
            }

            if (youtubeWorkHalted)
            {
                foreach (var plan in readyPlans)
                {
                    outcomes[plan.Request.ChannelId] = Retry(
                        plan.Request.ChannelId,
                        plan.PlaylistCalls,
                        plan.DurationCalls);
                }
                readyPlans.Clear();
            }

            var durationFailed = false;
            var durationFailureDisposition = ChannelRefreshDisposition.RetryTransient;
            foreach (var chunk in readyPlans
                .SelectMany(plan => plan.NewVideoIds)
                .Distinct(StringComparer.Ordinal)
                .Chunk(50))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork = true;
                    durationFailed = true;
                    break;
                }

                try
                {
                    var fetched = await _youtubeService.GetVideoDurationsByIdAsync(chunk, cancellationToken);
                    result.DurationCallCount++;
                    foreach (var duration in fetched)
                    {
                        durationsById[duration.Key] = duration.Value;
                    }

                    foreach (var plan in readyPlans.Where(plan => plan.NewVideoIds.Any(chunk.Contains)))
                    {
                        plan.DurationCalls++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork = true;
                    durationFailed = true;
                    break;
                }
                catch (YoutubePermanentException)
                {
                    durationFailed = true;
                    durationFailureDisposition = ChannelRefreshDisposition.FailedPermanent;
                    break;
                }
                catch (Exception exception) when (IsTransient(exception))
                {
                    durationFailed = true;
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "YouTube duration request failed permanently.");
                    durationFailed = true;
                    durationFailureDisposition = ChannelRefreshDisposition.FailedPermanent;
                    break;
                }
            }

            foreach (var plan in readyPlans)
            {
                if (durationFailed && plan.NewVideoIds.Any(id => !durationsById.ContainsKey(id)))
                {
                    outcomes[plan.Request.ChannelId] = new ChannelRefreshOutcome(
                        plan.Request.ChannelId,
                        durationFailureDisposition,
                        plan.PlaylistCalls,
                        plan.DurationCalls);
                    continue;
                }

                await MergeAndSaveAsync(plan, durationsById, outcomes);
            }

            foreach (var request in selected.Where(request => !outcomes.ContainsKey(request.ChannelId)))
            {
                outcomes[request.ChannelId] = Retry(request.ChannelId, 0, 0);
            }

            return CompleteResult(result, outcomes);
        }

        private async Task FetchPlaylistIncrementallyAsync(
            PlaylistPlan plan,
            CancellationToken cancellationToken)
        {
            var cachedIds = plan.CachedVideos
                .Select(video => video.VideoId)
                .ToHashSet(StringComparer.Ordinal);
            var scanned = new List<YoutubeVideo>();
            var earliestPublishedAt = _clock.UtcNow.Subtract(Constants.VideoMaxAge);
            string pageToken = null;
            var overlapFound = false;
            do
            {
                var page = await _youtubeService.GetPlaylistVideoPageAsync(
                    plan.Channel.PlaylistId,
                    pageToken,
                    cancellationToken);
                plan.PlaylistCalls++;
                var pageVideos = page.Videos
                    .Where(video => video.ChannelId == plan.Channel.Id)
                    .ToList();
                scanned.AddRange(pageVideos);
                overlapFound = pageVideos.Any(video => cachedIds.Contains(video.Id));
                pageToken = page.NextPageToken;
            } while (pageToken != null
                && !overlapFound
                && scanned
                    .Where(video => video.PublishedAt >= earliestPublishedAt)
                    .Select(video => video.Id)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                    < _options.MaximumVideosPerChannel
                && plan.PlaylistCalls < _options.MaximumPlaylistPages);

            if (pageToken != null && !overlapFound && plan.PlaylistCalls >= _options.MaximumPlaylistPages)
            {
                _logger.LogWarning(
                    "YouTube uploads scan limit reached. ChannelId={ChannelId}; Pages={Pages}.",
                    plan.Channel.Id,
                    plan.PlaylistCalls);
            }

            plan.ScannedVideos = scanned;
            plan.NewVideoIds = scanned
                .Where(video => video.PublishedAt >= earliestPublishedAt)
                .Select(video => video.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && !cachedIds.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private async Task MergeAndSaveAsync(
            PlaylistPlan plan,
            IReadOnlyDictionary<string, TimeSpan> durationsById,
            IDictionary<string, ChannelRefreshOutcome> outcomes)
        {
            var earliestPublishedAt = _clock.UtcNow.Subtract(Constants.VideoMaxAge);
            var merged = plan.CachedVideos
                .Where(video => video.PublishedAt >= earliestPublishedAt)
                .ToDictionary(video => video.VideoId, StringComparer.Ordinal);
            foreach (var video in plan.ScannedVideos)
            {
                if (string.IsNullOrWhiteSpace(video.Id) || video.PublishedAt < earliestPublishedAt)
                {
                    continue;
                }

                merged.TryGetValue(video.Id, out var cached);
                var duration = default(TimeSpan);
                if (cached == null && !durationsById.TryGetValue(video.Id, out duration))
                {
                    continue;
                }

                merged[video.Id] = new ChannelVideo
                {
                    ChannelId = plan.Channel.Id,
                    VideoId = video.Id,
                    Title = video.Title,
                    Duration = cached?.Duration ?? duration,
                    PublishedAt = video.PublishedAt,
                    ThumbnailUrl = video.Thumbnail
                };
            }

            plan.Channel.Videos = merged.Values
                .OrderByDescending(video => video.PublishedAt)
                .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                .Take(_options.MaximumVideosPerChannel)
                .ToList();
            SetNextRefresh(plan.Channel);
            await SaveOutcomeAsync(
                plan.Request,
                new ChannelRefreshResult
                {
                    Channel = plan.Channel,
                    VideosRefreshed = true,
                    EarliestPublishedAt = earliestPublishedAt
                },
                ChannelRefreshDisposition.Refreshed,
                plan.PlaylistCalls,
                plan.DurationCalls,
                outcomes);
        }

        private async Task SaveOutcomeAsync(
            ChannelRefreshRequest request,
            ChannelRefreshResult refreshResult,
            ChannelRefreshDisposition successDisposition,
            int playlistCalls,
            int durationCalls,
            IDictionary<string, ChannelRefreshOutcome> outcomes)
        {
            try
            {
                await _channelRepository.SaveRefreshResultAsync(refreshResult, CancellationToken.None);
                outcomes[request.ChannelId] = new ChannelRefreshOutcome(
                    request.ChannelId,
                    successDisposition,
                    playlistCalls,
                    durationCalls);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Could not persist a refreshed channel. ChannelId={ChannelId}.",
                    request.ChannelId);
                outcomes[request.ChannelId] = Retry(request.ChannelId, playlistCalls, durationCalls);
            }
        }

        private Channel CreateMissingChannel(string id)
        {
            return new Channel
            {
                Id = id,
                Url = string.Format(Constants.YoutubeChannelUrl, id),
                Title = string.Empty,
                Thumbnail = string.Empty,
                PlaylistId = string.Empty,
                StaleAfter = _clock.UtcNow
            };
        }

        private void ApplyMetadata(Channel channel, YoutubeChannel metadata)
        {
            channel.Url = string.Format(Constants.YoutubeChannelUrl, metadata.Id);
            channel.Title = metadata.Title;
            channel.Thumbnail = metadata.Thumbnail;
            channel.PlaylistId = metadata.PlaylistId ?? string.Empty;
            channel.Status = ChannelStatus.Active;
            channel.StatusReason = ChannelStatusReason.None;
            channel.StatusUpdatedAt = null;
        }

        private void MarkUnavailable(Channel channel)
        {
            var now = _clock.UtcNow;
            channel.Status = ChannelStatus.Unavailable;
            channel.StatusReason = ChannelStatusReason.NotFound;
            channel.StatusUpdatedAt = now;
            channel.StaleAfter = now.Add(Constants.ChannelUnavailableStaleDelay);
        }

        private void SetNextRefresh(Channel channel)
        {
            channel.StaleAfter = _clock.UtcNowAfterRandomDelay(
                Constants.ChannelMaxAgeMin,
                Constants.ChannelMaxAgeMax);
        }

        private static bool IsTransient(Exception exception)
        {
            return exception is YoutubeTransientException
                || exception is YoutubeQuotaExceededException;
        }

        private static ChannelRefreshOutcome Retry(string channelId, int playlistCalls, int durationCalls)
        {
            return new ChannelRefreshOutcome(
                channelId,
                ChannelRefreshDisposition.RetryTransient,
                playlistCalls,
                durationCalls);
        }

        private static void AddRetryOutcomes(
            IEnumerable<ChannelRefreshRequest> requests,
            IDictionary<string, ChannelRefreshOutcome> outcomes)
        {
            foreach (var request in requests)
            {
                outcomes[request.ChannelId] = Retry(request.ChannelId, 0, 0);
            }
        }

        private static void AddPermanentOutcomes(
            IEnumerable<ChannelRefreshRequest> requests,
            IDictionary<string, ChannelRefreshOutcome> outcomes)
        {
            foreach (var request in requests)
            {
                outcomes[request.ChannelId] = new ChannelRefreshOutcome(
                    request.ChannelId,
                    ChannelRefreshDisposition.FailedPermanent,
                    0,
                    0);
            }
        }

        private static ChannelRefreshPipelineResult CompleteResult(
            ChannelRefreshPipelineResult result,
            IDictionary<string, ChannelRefreshOutcome> outcomes)
        {
            result.Outcomes = outcomes.Values.ToList();
            result.RefreshedChannelCount = result.Outcomes.Count(outcome =>
                outcome.Disposition == ChannelRefreshDisposition.Refreshed);
            result.UnavailableChannelCount = result.Outcomes.Count(outcome =>
                outcome.Disposition == ChannelRefreshDisposition.Unavailable);
            return result;
        }
    }
}
