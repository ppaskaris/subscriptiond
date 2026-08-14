using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class ChannelRefreshHostedService : HostedService
    {
        private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);
        private readonly IChannelRefreshQueue _queue;
        private readonly IChannelRefreshPipeline _pipeline;
        private readonly ILogger<ChannelRefreshHostedService> _logger;

        public ChannelRefreshHostedService(
            IChannelRefreshQueue queue,
            IChannelRefreshPipeline pipeline,
            ILogger<ChannelRefreshHostedService> logger)
        {
            _queue = queue;
            _pipeline = pipeline;
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
            var channelIds = await _queue.DequeueBatchAsync(
                Constants.ChannelRefreshBatchSize,
                cancellationToken);
            try
            {
                var result = await _pipeline.RefreshAsync(channelIds, cancellationToken);
                _queue.Complete(channelIds);
                _logger.LogInformation(
                    "Channel refresh completed. SelectedChannels={SelectedChannels}; MetadataCalls={MetadataCalls}; PlaylistCalls={PlaylistCalls}; DurationCalls={DurationCalls}; ChannelsRefreshed={ChannelsRefreshed}; ChannelsMarkedUnavailable={ChannelsMarkedUnavailable}; QueueDepth={QueueDepth}.",
                    result.SelectedChannelCount,
                    result.MetadataCallCount,
                    result.PlaylistCallCount,
                    result.DurationCallCount,
                    result.RefreshedChannelCount,
                    result.UnavailableChannelCount,
                    _queue.Count);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _queue.Requeue(channelIds);
                throw;
            }
            catch (Exception exception)
            {
                _queue.Requeue(channelIds);
                _logger.LogError(exception, "Channel refresh failed; queued IDs will be retried.");
                return false;
            }
        }
    }
}
