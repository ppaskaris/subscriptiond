using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class ChannelRefreshHostedService : BackgroundService
    {
        private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);
        private readonly IChannelRefreshQueue _queue;
        private readonly IChannelRefreshPipeline _pipeline;
        private readonly ILogger<ChannelRefreshHostedService> _logger;
        private readonly YoutubeSyncOptions _options;

        public ChannelRefreshHostedService(
            IChannelRefreshQueue queue,
            IChannelRefreshPipeline pipeline,
            IOptions<YoutubeSyncOptions> options,
            ILogger<ChannelRefreshHostedService> logger)
        {
            _queue = queue;
            _pipeline = pipeline;
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var succeeded = await RunOnceAsync(cancellationToken);
                if (!succeeded && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(ErrorDelay, cancellationToken);
                }
            }
        }

        internal async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
        {
            var requests = await _queue.DequeueBatchAsync(
                _options.CohortSize,
                cancellationToken);
            try
            {
                var result = await _pipeline.RefreshAsync(requests, cancellationToken);
                var outcomesById = result.Outcomes.ToDictionary(
                    outcome => outcome.ChannelId,
                    StringComparer.Ordinal);
                var retryIds = requests
                    .Where(request => !outcomesById.TryGetValue(request.ChannelId, out var outcome)
                        || outcome.Disposition == ChannelRefreshDisposition.RetryTransient)
                    .Select(request => request.ChannelId)
                    .ToHashSet(StringComparer.Ordinal);
                _queue.Complete(requests
                    .Where(request => !retryIds.Contains(request.ChannelId))
                    .Select(request => request.ChannelId)
                    .ToList());
                _queue.Requeue(requests
                    .Where(request => retryIds.Contains(request.ChannelId))
                    .ToList());
                _logger.LogInformation(
                    "Channel refresh completed. SelectedChannels={SelectedChannels}; MetadataCalls={MetadataCalls}; PlaylistCalls={PlaylistCalls}; DurationCalls={DurationCalls}; ChannelsRefreshed={ChannelsRefreshed}; ChannelsMarkedUnavailable={ChannelsMarkedUnavailable}; ChannelsRequeued={ChannelsRequeued}; PermanentFailures={PermanentFailures}; QueueDepth={QueueDepth}.",
                    result.SelectedChannelCount,
                    result.MetadataCallCount,
                    result.PlaylistCallCount,
                    result.DurationCallCount,
                    result.RefreshedChannelCount,
                    result.UnavailableChannelCount,
                    retryIds.Count,
                    result.Outcomes.Count(outcome =>
                        outcome.Disposition == ChannelRefreshDisposition.FailedPermanent),
                    _queue.Count);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _queue.Requeue(requests);
                throw;
            }
            catch (Exception exception)
            {
                _queue.Requeue(requests);
                _logger.LogError(exception, "Channel refresh failed; queued IDs will be retried.");
                return false;
            }
        }
    }
}
