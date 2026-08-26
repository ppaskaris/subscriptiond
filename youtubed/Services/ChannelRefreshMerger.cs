using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using youtubed.Domain;

namespace youtubed.Services
{
    internal sealed record ChannelRefreshMergeResult(
        ChannelRefreshWorkItem WorkItem,
        ChannelRefreshDisposition Disposition,
        ChannelRefreshResult RefreshResult,
        int PlaylistCalls,
        int DurationCalls);

    public sealed class ChannelRefreshMerger
    {
        private readonly IAppClock _clock;
        private readonly YoutubeSyncOptions _options;

        public ChannelRefreshMerger(IAppClock clock, IOptions<YoutubeSyncOptions> options)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        internal IReadOnlyList<ChannelRefreshMergeResult> MergePlaylistStage(
            IReadOnlyList<YoutubePlaylistScanResult> scans)
        {
            ArgumentNullException.ThrowIfNull(scans);
            return scans
                .Where(scan => scan.Disposition != YoutubePlaylistScanDisposition.Success
                    || scan.NewVideoIds.Count == 0)
                .OrderBy(scan => scan.WorkItem.SelectedIndex)
                .Select(scan => MergeOne(scan, EmptyDurationFetchResult))
                .ToList();
        }

        internal IReadOnlyList<ChannelRefreshMergeResult> MergeDurationStage(
            IReadOnlyList<YoutubePlaylistScanResult> scans,
            YoutubeDurationFetchResult durations)
        {
            ArgumentNullException.ThrowIfNull(scans);
            ArgumentNullException.ThrowIfNull(durations);
            return scans
                .Where(scan => scan.Disposition == YoutubePlaylistScanDisposition.Success
                    && scan.NewVideoIds.Count > 0)
                .OrderBy(scan => scan.WorkItem.SelectedIndex)
                .Select(scan => MergeOne(scan, durations))
                .ToList();
        }

        private static YoutubeDurationFetchResult EmptyDurationFetchResult { get; } =
            new(
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal),
                new Dictionary<int, YoutubeDurationDependencyResult>(),
                0);

        private ChannelRefreshMergeResult MergeOne(
            YoutubePlaylistScanResult scan,
            YoutubeDurationFetchResult durations)
        {
            if (scan.Disposition == YoutubePlaylistScanDisposition.PermanentFailure)
            {
                return Failed(scan, ChannelRefreshDisposition.FailedPermanent, 0);
            }
            if (scan.Disposition != YoutubePlaylistScanDisposition.Success)
            {
                return Failed(scan, ChannelRefreshDisposition.RetryTransient, 0);
            }

            var durationCalls = 0;
            if (scan.NewVideoIds.Count > 0)
            {
                if (!durations.DependenciesBySelectedIndex.TryGetValue(
                    scan.WorkItem.SelectedIndex,
                    out var dependency))
                {
                    return Failed(scan, ChannelRefreshDisposition.RetryTransient, 0);
                }
                durationCalls = dependency.SuccessfulCalls;
                if (dependency.Disposition != YoutubeDurationDisposition.Complete)
                {
                    var disposition = dependency.Disposition == YoutubeDurationDisposition.PermanentFailure
                        ? ChannelRefreshDisposition.FailedPermanent
                        : ChannelRefreshDisposition.RetryTransient;
                    return Failed(scan, disposition, durationCalls);
                }
            }

            var earliestPublishedAt = _clock.UtcNow.Subtract(Constants.VideoMaxAge);
            var merged = scan.WorkItem.CachedVideos
                .Where(video => video.PublishedAt >= earliestPublishedAt)
                .ToDictionary(video => video.VideoId, StringComparer.Ordinal);
            foreach (var video in scan.ScannedVideos)
            {
                if (string.IsNullOrWhiteSpace(video.Id) || video.PublishedAt < earliestPublishedAt)
                {
                    continue;
                }
                merged.TryGetValue(video.Id, out var cached);
                var fetchedDuration = default(TimeSpan);
                if (cached == null
                    && !durations.DurationsById.TryGetValue(video.Id, out fetchedDuration))
                {
                    continue;
                }
                merged[video.Id] = new ChannelVideo
                {
                    ChannelId = scan.WorkItem.Channel.Id,
                    VideoId = video.Id,
                    Title = video.Title,
                    Duration = cached?.Duration ?? fetchedDuration,
                    PublishedAt = video.PublishedAt,
                    ThumbnailUrl = video.Thumbnail
                };
            }

            scan.WorkItem.Channel.Videos = merged.Values
                .OrderByDescending(video => video.PublishedAt)
                .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                .Take(_options.MaximumVideosPerChannel)
                .ToList();
            scan.WorkItem.Channel.StaleAfter = _clock.UtcNowAfterRandomDelay(
                Constants.ChannelMaxAgeMin,
                Constants.ChannelMaxAgeMax);
            return new ChannelRefreshMergeResult(
                scan.WorkItem,
                ChannelRefreshDisposition.Refreshed,
                new ChannelRefreshResult
                {
                    Channel = scan.WorkItem.Channel,
                    VideosRefreshed = true,
                    EarliestPublishedAt = earliestPublishedAt
                },
                scan.SuccessfulCalls,
                durationCalls);
        }

        private static ChannelRefreshMergeResult Failed(
            YoutubePlaylistScanResult scan,
            ChannelRefreshDisposition disposition,
            int durationCalls) =>
            new(
                scan.WorkItem,
                disposition,
                null,
                scan.SuccessfulCalls,
                durationCalls);
    }
}
