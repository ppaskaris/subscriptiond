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
        private readonly IChannelRepository _channelRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;
        private readonly YoutubeSyncOptions _options;
        private readonly YoutubePlaylistScanner _playlistScanner;
        private readonly YoutubeDurationFetcher _durationFetcher;
        private readonly ChannelRefreshMerger _merger;
        private readonly ILogger<ChannelRefreshPipeline> _logger;

        public ChannelRefreshPipeline(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService,
            IAppClock clock,
            IOptions<YoutubeSyncOptions> options,
            YoutubePlaylistScanner playlistScanner,
            YoutubeDurationFetcher durationFetcher,
            ChannelRefreshMerger merger,
            ILogger<ChannelRefreshPipeline> logger)
        {
            _channelRepository = channelRepository
                ?? throw new ArgumentNullException(nameof(channelRepository));
            _youtubeService = youtubeService ?? throw new ArgumentNullException(nameof(youtubeService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _playlistScanner = playlistScanner
                ?? throw new ArgumentNullException(nameof(playlistScanner));
            _durationFetcher = durationFetcher
                ?? throw new ArgumentNullException(nameof(durationFetcher));
            _merger = merger ?? throw new ArgumentNullException(nameof(merger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ValidateOptions(_options);
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
            if (selected.Count == 0)
            {
                return result;
            }

            var outcomes = new ChannelRefreshOutcome[selected.Count];
            IReadOnlyList<Channel> cachedChannels;
            try
            {
                cachedChannels = await _channelRepository.GetBatchAsync(
                    selected.Select(request => request.ChannelId).ToList(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.CanceledBeforeStartingYoutubeCall = true;
                FillRetries(selected, outcomes);
                return CompleteResult(result, outcomes);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.CanceledBeforeStartingYoutubeCall = true;
                FillRetries(selected, outcomes);
                return CompleteResult(result, outcomes);
            }

            var cachedById = cachedChannels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
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
                FillRetries(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (YoutubePermanentException)
            {
                FillPermanent(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                FillRetries(selected, outcomes);
                return CompleteResult(result, outcomes);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "YouTube metadata request failed permanently.");
                FillPermanent(selected, outcomes);
                return CompleteResult(result, outcomes);
            }

            var prepared = new List<ChannelRefreshWorkItem>();
            for (var index = 0; index < selected.Count; index++)
            {
                var request = selected[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork = true;
                    outcomes[index] = Retry(request.ChannelId, 0, 0);
                    continue;
                }

                cachedById.TryGetValue(request.ChannelId, out var cached);
                if (!metadataById.TryGetValue(request.ChannelId, out var metadata))
                {
                    var unavailable = cached ?? CreateMissingChannel(request.ChannelId);
                    MarkUnavailable(unavailable);
                    outcomes[index] = await PersistAsync(
                        request,
                        new ChannelRefreshResult { Channel = unavailable, VideosRefreshed = false },
                        ChannelRefreshDisposition.Unavailable,
                        0,
                        0);
                    continue;
                }

                var channel = cached ?? CreateMissingChannel(request.ChannelId);
                var cachedVideos = channel.Videos?.ToList() ?? new List<ChannelVideo>();
                ApplyMetadata(channel, metadata);
                if (string.IsNullOrWhiteSpace(channel.PlaylistId))
                {
                    SetNextRefresh(channel);
                    outcomes[index] = await PersistAsync(
                        request,
                        new ChannelRefreshResult { Channel = channel, VideosRefreshed = false },
                        ChannelRefreshDisposition.Refreshed,
                        0,
                        0);
                    continue;
                }

                prepared.Add(new ChannelRefreshWorkItem(
                    index,
                    request,
                    channel,
                    cachedVideos));
            }

            var scans = await _playlistScanner.ScanAsync(prepared, cancellationToken);
            result.PlaylistCallCount = scans.Sum(scan => scan.SuccessfulCalls);
            await CompleteBarrierAsync(
                _merger.MergePlaylistStage(scans),
                outcomes);

            var durations = await _durationFetcher.FetchAsync(scans, cancellationToken);
            result.DurationCallCount = durations.SuccessfulCallCount;
            await CompleteBarrierAsync(
                _merger.MergeDurationStage(scans, durations),
                outcomes);

            result.CanceledDuringYoutubeWork |= cancellationToken.IsCancellationRequested;
            for (var index = 0; index < outcomes.Length; index++)
            {
                outcomes[index] ??= Retry(selected[index].ChannelId, 0, 0);
            }
            return CompleteResult(result, outcomes);
        }

        private async Task CompleteBarrierAsync(
            IReadOnlyList<ChannelRefreshMergeResult> merged,
            ChannelRefreshOutcome[] outcomes)
        {
            foreach (var item in merged.Where(item => item.RefreshResult != null))
            {
                outcomes[item.WorkItem.SelectedIndex] = await PersistAsync(
                    item.WorkItem.Request,
                    item.RefreshResult,
                    item.Disposition,
                    item.PlaylistCalls,
                    item.DurationCalls);
            }
            foreach (var item in merged.Where(item => item.RefreshResult == null))
            {
                outcomes[item.WorkItem.SelectedIndex] = new ChannelRefreshOutcome(
                    item.WorkItem.Request.ChannelId,
                    item.Disposition,
                    item.PlaylistCalls,
                    item.DurationCalls);
            }
        }

        private async Task<ChannelRefreshOutcome> PersistAsync(
            ChannelRefreshRequest request,
            ChannelRefreshResult refreshResult,
            ChannelRefreshDisposition successDisposition,
            int playlistCalls,
            int durationCalls)
        {
            try
            {
                await _channelRepository.SaveRefreshResultAsync(
                    refreshResult,
                    CancellationToken.None);
                return new ChannelRefreshOutcome(
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
                return Retry(request.ChannelId, playlistCalls, durationCalls);
            }
        }

        private Channel CreateMissingChannel(string id) => new()
        {
            Id = id,
            Url = string.Format(Constants.YoutubeChannelUrl, id),
            Title = string.Empty,
            Thumbnail = string.Empty,
            PlaylistId = string.Empty,
            StaleAfter = _clock.UtcNow
        };

        private static void ApplyMetadata(Channel channel, YoutubeChannel metadata)
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

        private static bool IsTransient(Exception exception) =>
            exception is YoutubeTransientException || exception is YoutubeQuotaExceededException;

        private static ChannelRefreshOutcome Retry(
            string channelId,
            int playlistCalls,
            int durationCalls) =>
            new(channelId, ChannelRefreshDisposition.RetryTransient, playlistCalls, durationCalls);

        private static void FillRetries(
            IReadOnlyList<ChannelRefreshRequest> selected,
            ChannelRefreshOutcome[] outcomes)
        {
            for (var index = 0; index < selected.Count; index++)
            {
                outcomes[index] = Retry(selected[index].ChannelId, 0, 0);
            }
        }

        private static void FillPermanent(
            IReadOnlyList<ChannelRefreshRequest> selected,
            ChannelRefreshOutcome[] outcomes)
        {
            for (var index = 0; index < selected.Count; index++)
            {
                outcomes[index] = new ChannelRefreshOutcome(
                    selected[index].ChannelId,
                    ChannelRefreshDisposition.FailedPermanent,
                    0,
                    0);
            }
        }

        private static ChannelRefreshPipelineResult CompleteResult(
            ChannelRefreshPipelineResult result,
            ChannelRefreshOutcome[] outcomes)
        {
            result.Outcomes = outcomes.Where(outcome => outcome != null).ToList();
            result.RefreshedChannelCount = result.Outcomes.Count(outcome =>
                outcome.Disposition == ChannelRefreshDisposition.Refreshed);
            result.UnavailableChannelCount = result.Outcomes.Count(outcome =>
                outcome.Disposition == ChannelRefreshDisposition.Unavailable);
            return result;
        }

        private static void ValidateOptions(YoutubeSyncOptions options)
        {
            if (options.CohortSize <= 0
                || options.CohortSize > 50
                || options.MaximumConcurrentRequests <= 0
                || options.MaximumPlaylistPages <= 0
                || options.MaximumVideosPerChannel <= 0
                || options.MaximumVideosPerChannel > Constants.ListRenderMaxItems)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "YouTube sync cohorts must contain 1-50 channels, concurrency must be positive, and retained videos must remain within the document bound.");
            }
        }
    }
}
