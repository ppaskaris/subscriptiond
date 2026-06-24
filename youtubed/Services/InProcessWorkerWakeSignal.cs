using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class InProcessWorkerWakeSignal : IWorkerWakeSignal
    {
        private readonly object _lock = new object();
        private long _version;
        private TaskCompletionSource<bool> _signal = CreateSignal();

        public long Version
        {
            get
            {
                lock (_lock)
                {
                    return _version;
                }
            }
        }

        public void Pulse()
        {
            TaskCompletionSource<bool> signal;
            lock (_lock)
            {
                _version++;
                signal = _signal;
                _signal = CreateSignal();
            }

            signal.TrySetResult(true);
        }

        public async Task<bool> WaitAsync(
            long observedVersion,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Task signalTask;
            lock (_lock)
            {
                if (_version != observedVersion)
                {
                    return true;
                }

                signalTask = _signal.Task;
            }

            var delayTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(signalTask, delayTask);
            if (completed == signalTask)
            {
                return true;
            }

            await delayTask;
            return false;
        }

        private static TaskCompletionSource<bool> CreateSignal()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
