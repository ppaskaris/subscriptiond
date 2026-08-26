using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public readonly record struct YoutubeCallPolicy(bool WaitForCooldown)
    {
        public static YoutubeCallPolicy Foreground { get; } = new(false);

        public static YoutubeCallPolicy Refresh { get; } = new(true);
    }

    public interface IYoutubeCallInvoker
    {
        Task<T> InvokeAsync<T>(
            Func<CancellationToken, Task<T>> call,
            YoutubeCallPolicy policy,
            CancellationToken cancellationToken);
    }
}
