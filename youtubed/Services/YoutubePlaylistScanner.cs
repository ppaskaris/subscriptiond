using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    internal enum YoutubePlaylistScanDisposition
    {
        Success,
        PermanentFailure,
        RetryTransient
    }

    internal sealed record YoutubePlaylistScanResult(
        ChannelRefreshWorkItem WorkItem,
        YoutubePlaylistScanDisposition Disposition,
        IReadOnlyList<YoutubeVideo> ScannedVideos,
        IReadOnlyList<string> NewVideoIds,
        int SuccessfulCalls);

    public sealed class YoutubePlaylistScanner
    {
        private readonly IYoutubeService _youtubeService;
        private readonly IAppClock _clock;
        private readonly YoutubeSyncOptions _options;
        private readonly ILogger<YoutubePlaylistScanner> _logger;

        public YoutubePlaylistScanner(
            IYoutubeService youtubeService,
            IAppClock clock,
            IOptions<YoutubeSyncOptions> options,
            ILogger<YoutubePlaylistScanner> logger)
        {
            _youtubeService = youtubeService ?? throw new ArgumentNullException(nameof(youtubeService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal async Task<IReadOnlyList<YoutubePlaylistScanResult>> ScanAsync(
            IReadOnlyList<ChannelRefreshWorkItem> workItems,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(workItems);
            if (workItems.Count == 0)
            {
                return Array.Empty<YoutubePlaylistScanResult>();
            }

            var results = new YoutubePlaylistScanResult[workItems.Count];
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, workItems.Count),
                    new ParallelOptions
                    {
                        CancellationToken = stop.Token,
                        MaxDegreeOfParallelism = _options.MaximumConcurrentRequests
                    },
                    async (index, token) =>
                    {
                        var result = await ScanOneAsync(workItems[index], token);
                        results[index] = result;
                        if (result.Disposition == YoutubePlaylistScanDisposition.RetryTransient)
                        {
                            await stop.CancelAsync();
                        }
                    });
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Completed slots remain useful; pending slots are retryable below.
            }

            for (var index = 0; index < results.Length; index++)
            {
                results[index] ??= Retry(workItems[index], successfulCalls: 0);
            }
            return results;
        }

        private async Task<YoutubePlaylistScanResult> ScanOneAsync(
            ChannelRefreshWorkItem workItem,
            CancellationToken cancellationToken)
        {
            var successfulCalls = 0;
            try
            {
                var cachedIds = workItem.CachedVideos
                    .Select(video => video.VideoId)
                    .ToHashSet(StringComparer.Ordinal);
                var scanned = new List<YoutubeVideo>();
                var earliestPublishedAt = _clock.UtcNow.Subtract(Constants.VideoMaxAge);
                string pageToken = null;
                var overlapFound = false;
                do
                {
                    var page = await _youtubeService.GetPlaylistVideoPageAsync(
                        workItem.Channel.PlaylistId,
                        pageToken,
                        cancellationToken);
                    successfulCalls++;
                    var pageVideos = page.Videos
                        .Where(video => video.ChannelId == workItem.Channel.Id)
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
                        .Count() < _options.MaximumVideosPerChannel
                    && successfulCalls < _options.MaximumPlaylistPages);

                if (pageToken != null
                    && !overlapFound
                    && successfulCalls >= _options.MaximumPlaylistPages)
                {
                    _logger.LogWarning(
                        "YouTube uploads scan limit reached. ChannelId={ChannelId}; Pages={Pages}.",
                        workItem.Channel.Id,
                        successfulCalls);
                }

                var newVideoIds = scanned
                    .Where(video => video.PublishedAt >= earliestPublishedAt)
                    .Select(video => video.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id) && !cachedIds.Contains(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                return new YoutubePlaylistScanResult(
                    workItem,
                    YoutubePlaylistScanDisposition.Success,
                    scanned,
                    newVideoIds,
                    successfulCalls);
            }
            catch (OperationCanceledException)
            {
                return Retry(workItem, successfulCalls);
            }
            catch (YoutubePermanentException)
            {
                return Permanent(workItem, successfulCalls);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                return Retry(workItem, successfulCalls);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "YouTube playlist request failed permanently. ChannelId={ChannelId}.",
                    workItem.Request.ChannelId);
                return Permanent(workItem, successfulCalls);
            }
        }

        private static YoutubePlaylistScanResult Retry(
            ChannelRefreshWorkItem item,
            int successfulCalls) =>
            new(
                item,
                YoutubePlaylistScanDisposition.RetryTransient,
                Array.Empty<YoutubeVideo>(),
                Array.Empty<string>(),
                successfulCalls);

        private static YoutubePlaylistScanResult Permanent(
            ChannelRefreshWorkItem item,
            int successfulCalls) =>
            new(
                item,
                YoutubePlaylistScanDisposition.PermanentFailure,
                Array.Empty<YoutubeVideo>(),
                Array.Empty<string>(),
                successfulCalls);

        private static bool IsTransient(Exception exception) =>
            exception is YoutubeTransientException || exception is YoutubeQuotaExceededException;
    }
}
