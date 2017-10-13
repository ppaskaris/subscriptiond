using System;
using System.Collections.Generic;
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
                    var channelId = await _channelService.GetNextStaleIdOrDefaultAsync();
                    if (channelId != null)
                    {
                        await _channelVideoService.RefreshVideosAsync(channelId);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("I fucked up.");
                    Console.WriteLine(ex.ToString());
                }
                await Task.Delay(Constants.UpdateFrequency, cancellationToken);
            }
        }
    }
}
