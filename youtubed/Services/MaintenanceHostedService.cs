using Humanizer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public class MaintenanceHostedService : HostedService
    {
        private readonly IChannelService _channelService;
        private readonly IListService _listService;
        private readonly IShareLinkService _shareLinkService;
        private readonly IAppClock _clock;
        private readonly ILogger _logger;

        public MaintenanceHostedService(
            IChannelService channelService,
            IListService listService,
            IShareLinkService shareLinkService,
            IAppClock clock,
            ILogger<MaintenanceHostedService> logger)
        {
            _channelService = channelService;
            _listService = listService;
            _shareLinkService = shareLinkService;
            _clock = clock;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Checking for expired lists.");
                    var removeCount = await _listService.RemoveExpiredListsAsync();
                    if (removeCount > 0)
                    {
                        _logger.LogInformation("Removed {0} expired lists.", removeCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception thrown while removing expired lists.");
                    _logger.LogError(ex.ToString());
                }
                try
                {
                    _logger.LogInformation("Checking for expired share links.");
                    var removeCount = await _shareLinkService.RemoveExpiredShareLinksAsync();
                    if (removeCount > 0)
                    {
                        _logger.LogInformation("Removed {0} expired share links.", removeCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception thrown while removing expired share links.");
                    _logger.LogError(ex.ToString());
                }
                try
                {
                    _logger.LogInformation("Checking for orphan channels.");
                    var removeCount = await _channelService.RemoveOrphanChannelsAsync();
                    if (removeCount > 0)
                    {
                        _logger.LogInformation("Removed {0} orphan channels.", removeCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception thrown while removing orphan channels.");
                    _logger.LogError(ex.ToString());
                }
                var delay = _clock.RandomDelay(
                    Constants.MaintenanceFrequencyMin,
                    Constants.MaintenanceFrequencyMax);
                _logger.LogInformation("Performing maintenance again in {0}.", delay.Humanize());
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
