using System;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;

namespace youtubed.Persistence
{
    public interface IWorkerStateStore
    {
        Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken);

        Task ForceChannelRefreshAsync(CancellationToken cancellationToken);

        Task CompleteChannelRefreshPassAsync(
            DateTimeOffset? observedNextChannelRefreshAt,
            DateTimeOffset? nextChannelRefreshAt,
            CancellationToken cancellationToken);

        Task CompletePurgeAsync(
            DateTimeOffset nextPurgeAt,
            CancellationToken cancellationToken);
    }
}
