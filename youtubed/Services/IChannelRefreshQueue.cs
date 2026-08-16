using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IChannelRefreshQueue
    {
        int Count { get; }
        bool TryEnqueue(ChannelRefreshRequest request);
        int Enqueue(IReadOnlyCollection<ChannelRefreshRequest> requests);
        Task<IReadOnlyList<ChannelRefreshRequest>> DequeueBatchAsync(
            int maximumCount,
            CancellationToken cancellationToken);
        void Complete(IReadOnlyCollection<string> channelIds);
        void Requeue(IReadOnlyCollection<ChannelRefreshRequest> requests);
    }
}
