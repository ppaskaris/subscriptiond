using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IWorkerWakeSignal
    {
        long Version { get; }

        void Pulse();

        Task<bool> WaitAsync(
            long observedVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken);
    }
}
