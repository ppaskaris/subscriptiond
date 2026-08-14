using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class ChannelRefreshQueue : IChannelRefreshQueue, IDisposable
    {
        private readonly object _sync = new object();
        private readonly int _capacity;
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly HashSet<string> _pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _trackedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);

        public ChannelRefreshQueue()
            : this(Constants.ChannelRefreshQueueCapacity)
        {
        }

        internal ChannelRefreshQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _trackedIds.Count;
                }
            }
        }

        public bool TryEnqueue(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return false;
            }

            lock (_sync)
            {
                if (_trackedIds.Contains(channelId) || _trackedIds.Count >= _capacity)
                {
                    return false;
                }

                _trackedIds.Add(channelId);
                AddPending(channelId);
                return true;
            }
        }

        public async Task<IReadOnlyList<string>> DequeueBatchAsync(
            int maximumCount,
            CancellationToken cancellationToken)
        {
            if (maximumCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCount));
            }

            await _available.WaitAsync(cancellationToken);
            lock (_sync)
            {
                var result = new List<string>(Math.Min(maximumCount, _pending.Count));
                TakePending(result);
                while (result.Count < maximumCount && _available.Wait(0))
                {
                    TakePending(result);
                }

                return result;
            }
        }

        public void Complete(IReadOnlyCollection<string> channelIds)
        {
            if (channelIds == null)
            {
                throw new ArgumentNullException(nameof(channelIds));
            }

            lock (_sync)
            {
                foreach (var channelId in channelIds)
                {
                    if (!_pendingIds.Contains(channelId))
                    {
                        _trackedIds.Remove(channelId);
                    }
                }
            }
        }

        public void Requeue(IReadOnlyCollection<string> channelIds)
        {
            if (channelIds == null)
            {
                throw new ArgumentNullException(nameof(channelIds));
            }

            lock (_sync)
            {
                foreach (var channelId in channelIds)
                {
                    if (_trackedIds.Contains(channelId) && !_pendingIds.Contains(channelId))
                    {
                        AddPending(channelId);
                    }
                }
            }
        }

        public void Dispose()
        {
            _available.Dispose();
        }

        private void AddPending(string channelId)
        {
            _pending.Enqueue(channelId);
            _pendingIds.Add(channelId);
            _available.Release();
        }

        private void TakePending(ICollection<string> result)
        {
            var channelId = _pending.Dequeue();
            _pendingIds.Remove(channelId);
            result.Add(channelId);
        }
    }
}
