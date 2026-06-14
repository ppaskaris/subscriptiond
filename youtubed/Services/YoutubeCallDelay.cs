using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class YoutubeCallDelay : IYoutubeCallDelay
    {
        public Task DelayAsync(CancellationToken cancellationToken)
        {
            return Task.Delay(Constants.YoutubeCallDelay, cancellationToken);
        }
    }
}
