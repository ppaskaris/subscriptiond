using Humanizer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class UpdateChannelHostedService : HostedService
    {
        private readonly IChannelService _channelService;
        private readonly IChannelVideoService _channelVideoService;
        private readonly ILogger _logger;

        public UpdateChannelHostedService(
            IChannelService channelService,
            IChannelVideoService channelVideoService,
            ILogger<UpdateChannelHostedService> logger)
        {
            _channelService = channelService;
            _channelVideoService = channelVideoService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Checking for stale channels.");
                    var channel = await _channelService.GetNextStaleChannelOrDefaultAsync();
                    if (channel != null)
                    {
                        _logger.LogInformation("Refreshing channel {0}.", channel.Id);
                        await _channelVideoService.RefreshVideosAsync(channel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception thrown while updating channels.");
                    _logger.LogError(ex.ToString());
                }
                var delay = Constants.RandomlyBetween(
                    Constants.ChannelUpdateFrequencyMin,
                    Constants.ChannelUpdateFrequencyMax);
                _logger.LogInformation("Updating channels again in {0}.", delay.Humanize());
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
