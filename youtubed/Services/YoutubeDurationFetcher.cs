using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    internal enum YoutubeDurationDisposition
    {
        Complete,
        PermanentFailure,
        RetryTransient
    }

    internal sealed record YoutubeDurationDependencyResult(
        YoutubeDurationDisposition Disposition,
        int SuccessfulCalls);

    internal sealed record YoutubeDurationFetchResult(
        IReadOnlyDictionary<string, TimeSpan> DurationsById,
        IReadOnlyDictionary<int, YoutubeDurationDependencyResult> DependenciesBySelectedIndex,
        int SuccessfulCallCount);

    public sealed class YoutubeDurationFetcher
    {
        private enum ChunkDisposition
        {
            Success,
            PermanentFailure,
            RetryTransient
        }

        private sealed record Chunk(int Index, IReadOnlyList<string> VideoIds);

        private sealed record ChunkResult(
            Chunk Chunk,
            ChunkDisposition Disposition,
            IReadOnlyDictionary<string, TimeSpan> Durations);

        private static IReadOnlyDictionary<string, TimeSpan> EmptyDurations { get; } =
            new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        private readonly IYoutubeService _youtubeService;
        private readonly YoutubeSyncOptions _options;
        private readonly ILogger<YoutubeDurationFetcher> _logger;

        public YoutubeDurationFetcher(
            IYoutubeService youtubeService,
            IOptions<YoutubeSyncOptions> options,
            ILogger<YoutubeDurationFetcher> logger)
        {
            _youtubeService = youtubeService ?? throw new ArgumentNullException(nameof(youtubeService));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal async Task<YoutubeDurationFetchResult> FetchAsync(
            IReadOnlyList<YoutubePlaylistScanResult> scans,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scans);
            var ready = scans
                .Where(scan => scan.Disposition == YoutubePlaylistScanDisposition.Success
                    && scan.NewVideoIds.Count > 0)
                .OrderBy(scan => scan.WorkItem.SelectedIndex)
                .ToList();
            var orderedIds = ready
                .SelectMany(scan => scan.NewVideoIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var chunks = orderedIds
                .Chunk(50)
                .Select((ids, index) => new Chunk(index, ids.ToList()))
                .ToList();
            if (chunks.Count == 0)
            {
                return new YoutubeDurationFetchResult(
                    EmptyDurations,
                    new Dictionary<int, YoutubeDurationDependencyResult>(),
                    0);
            }

            var chunkByVideoId = chunks
                .SelectMany(chunk => chunk.VideoIds.Select(id => (id, chunk.Index)))
                .ToDictionary(item => item.id, item => item.Index, StringComparer.Ordinal);
            var dependencies = ready.ToDictionary(
                scan => scan.WorkItem.SelectedIndex,
                scan => scan.NewVideoIds
                    .Select(id => chunkByVideoId[id])
                    .ToHashSet());
            var chunkResults = new ChunkResult[chunks.Count];
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, chunks.Count),
                    new ParallelOptions
                    {
                        CancellationToken = stop.Token,
                        MaxDegreeOfParallelism = _options.MaximumConcurrentRequests
                    },
                    async (index, token) =>
                    {
                        var result = await FetchOneAsync(chunks[index], token);
                        chunkResults[index] = result;
                        if (result.Disposition == ChunkDisposition.RetryTransient)
                        {
                            await stop.CancelAsync();
                        }
                    });
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Completed chunks remain useful; pending chunks are retryable below.
            }

            for (var index = 0; index < chunkResults.Length; index++)
            {
                chunkResults[index] ??= new ChunkResult(
                    chunks[index],
                    ChunkDisposition.RetryTransient,
                    EmptyDurations);
            }

            var durations = chunkResults
                .Where(chunk => chunk.Disposition == ChunkDisposition.Success)
                .SelectMany(chunk => chunk.Durations)
                .GroupBy(duration => duration.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
            var dependencyResults = new Dictionary<int, YoutubeDurationDependencyResult>();
            foreach (var dependency in dependencies)
            {
                var required = dependency.Value.Select(index => chunkResults[index]).ToList();
                var successfulCalls = required.Count(chunk => chunk.Disposition == ChunkDisposition.Success);
                var disposition = required.Any(chunk => chunk.Disposition == ChunkDisposition.PermanentFailure)
                    ? YoutubeDurationDisposition.PermanentFailure
                    : required.Any(chunk => chunk.Disposition != ChunkDisposition.Success)
                        ? YoutubeDurationDisposition.RetryTransient
                        : YoutubeDurationDisposition.Complete;
                dependencyResults[dependency.Key] = new YoutubeDurationDependencyResult(
                    disposition,
                    successfulCalls);
            }
            return new YoutubeDurationFetchResult(
                durations,
                dependencyResults,
                chunkResults.Count(chunk => chunk.Disposition == ChunkDisposition.Success));
        }

        private async Task<ChunkResult> FetchOneAsync(Chunk chunk, CancellationToken cancellationToken)
        {
            try
            {
                var durations = await _youtubeService.GetVideoDurationsByIdAsync(
                    chunk.VideoIds,
                    cancellationToken);
                return new ChunkResult(chunk, ChunkDisposition.Success, durations);
            }
            catch (OperationCanceledException)
            {
                return new ChunkResult(chunk, ChunkDisposition.RetryTransient, EmptyDurations);
            }
            catch (YoutubePermanentException)
            {
                return new ChunkResult(chunk, ChunkDisposition.PermanentFailure, EmptyDurations);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                return new ChunkResult(chunk, ChunkDisposition.RetryTransient, EmptyDurations);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "YouTube duration request failed permanently.");
                return new ChunkResult(chunk, ChunkDisposition.PermanentFailure, EmptyDurations);
            }
        }

        private static bool IsTransient(Exception exception) =>
            exception is YoutubeTransientException || exception is YoutubeQuotaExceededException;
    }
}
