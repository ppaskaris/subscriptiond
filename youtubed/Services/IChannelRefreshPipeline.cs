using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IChannelRefreshPipeline
    {
        Task<ChannelRefreshPipelineResult> RefreshAsync(
            IReadOnlyCollection<string> channelIds,
            CancellationToken cancellationToken);
    }
}
