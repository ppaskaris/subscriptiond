using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IYoutubeCallDelay
    {
        Task DelayAsync(CancellationToken cancellationToken);
    }
}
