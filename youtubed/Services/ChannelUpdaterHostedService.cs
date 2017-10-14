using Humanizer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class ChannelUpdaterHostedService : HostedService
    {
        private readonly IChannelService _channelService;
        private readonly IChannelVideoService _channelVideoService;

        public ChannelUpdaterHostedService(
            IChannelService channelService,
            IChannelVideoService channelVideoService)
        {
            _channelService = channelService;
            _channelVideoService = channelVideoService;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Trace.TraceInformation("Checking for a stale channel ID.");
                    var channelId = await _channelService.GetNextStaleIdOrDefaultAsync();
                    if (channelId != null)
                    {
                        Trace.TraceInformation("Refreshing channel {0}", channelId);
                        await _channelVideoService.RefreshVideosAsync(channelId);
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Exception thrown while updatiung channels.");
                    Trace.TraceError(ex.ToString());
                }
                var delay = Constants.RandomlyBetween(
                    Constants.UpdateFrequencyMin,
                    Constants.UpdateFrequencyMax);
                Trace.TraceInformation("Checking again in {0}", delay.Humanize());
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
