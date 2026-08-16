using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IYoutubeRequestGate
    {
        YoutubeRequestGateSnapshot Snapshot { get; }

        Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> request,
            bool waitForCooldown,
            CancellationToken cancellationToken);
    }

    public sealed record YoutubeRequestGateSnapshot(
        long RequestAttempts,
        long Retries,
        long ThrottledWaits,
        long Cooldowns,
        long QuotaExhaustions);
}
