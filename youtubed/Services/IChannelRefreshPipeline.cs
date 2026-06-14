using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IChannelRefreshPipeline
    {
        Task<ChannelRefreshPipelineResult> RefreshStaleChannelsAsync(CancellationToken cancellationToken);
    }
}
