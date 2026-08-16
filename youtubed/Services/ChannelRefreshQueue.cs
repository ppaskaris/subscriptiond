using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class ChannelRefreshQueue : IChannelRefreshQueue, IDisposable
    {
        private sealed record PendingEntry(ChannelRefreshRequest Request, long Sequence);

        private readonly object _sync = new object();
        private readonly int _capacity;
        private readonly TimeSpan _coalescingWindow;
        private readonly ILogger<ChannelRefreshQueue> _logger;
        private readonly Dictionary<string, PendingEntry> _pending =
            new Dictionary<string, PendingEntry>(StringComparer.Ordinal);
        private readonly HashSet<string> _inFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _trackedIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0, 1);
        private long _sequence;

        public ChannelRefreshQueue()
            : this(Constants.ChannelRefreshQueueCapacity, TimeSpan.FromMilliseconds(100), null)
        {
        }

        public ChannelRefreshQueue(
            IOptions<YoutubeSyncOptions> options,
            ILogger<ChannelRefreshQueue> logger)
            : this(
                options?.Value.QueueCapacity ?? throw new ArgumentNullException(nameof(options)),
                options.Value.CoalescingWindow,
                logger)
        {
        }

        internal ChannelRefreshQueue(
            int capacity,
            TimeSpan? coalescingWindow = null,
            ILogger<ChannelRefreshQueue> logger = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _capacity = capacity;
            _coalescingWindow = coalescingWindow.GetValueOrDefault();
            _logger = logger;
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

        public bool TryEnqueue(ChannelRefreshRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChannelId))
            {
                return false;
            }

            lock (_sync)
            {
                if (_pending.TryGetValue(request.ChannelId, out var pending))
                {
                    var merged = Merge(pending.Request, request);
                    if (merged == pending.Request)
                    {
                        return false;
                    }

                    _pending[request.ChannelId] = pending with { Request = merged };
                    _logger?.LogDebug(
                        "Channel refresh request promoted. ChannelId={ChannelId}; Reason={Reason}.",
                        request.ChannelId,
                        merged.Reason);
                    return true;
                }

                if (_inFlight.Contains(request.ChannelId))
                {
                    if (request.Reason != ChannelRefreshReason.Forced)
                    {
                        return false;
                    }

                    AddPending(request);
                    return true;
                }

                if (_trackedIds.Count >= _capacity)
                {
                    _logger?.LogWarning(
                        "Channel refresh request dropped because the queue is full. ChannelId={ChannelId}; Reason={Reason}; Capacity={Capacity}.",
                        request.ChannelId,
                        request.Reason,
                        _capacity);
                    return false;
                }

                _trackedIds.Add(request.ChannelId);
                AddPending(request);
                return true;
            }
        }

        public int Enqueue(IReadOnlyCollection<ChannelRefreshRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);
            var accepted = 0;
            foreach (var request in requests
                .Where(request => request != null)
                .OrderBy(request => request.Reason)
                .ThenBy(request => request.StaleAfter ?? DateTimeOffset.MinValue)
                .ThenBy(request => request.ChannelId, StringComparer.Ordinal))
            {
                if (TryEnqueue(request))
                {
                    accepted++;
                }
            }

            return accepted;
        }

        public async Task<IReadOnlyList<ChannelRefreshRequest>> DequeueBatchAsync(
            int maximumCount,
            CancellationToken cancellationToken)
        {
            if (maximumCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumCount));
            }

            await _available.WaitAsync(cancellationToken);
            bool shouldCoalesce;
            lock (_sync)
            {
                shouldCoalesce = _pending.Count < maximumCount;
            }
            try
            {
                if (shouldCoalesce && _coalescingWindow > TimeSpan.Zero)
                {
                    await Task.Delay(_coalescingWindow, cancellationToken);
                }
            }
            catch
            {
                lock (_sync)
                {
                    SignalIfPending();
                }
                throw;
            }

            lock (_sync)
            {
                var selected = _pending.Values
                    .OrderBy(entry => entry.Request.Reason)
                    .ThenBy(entry => entry.Request.StaleAfter ?? DateTimeOffset.MinValue)
                    .ThenBy(entry => entry.Sequence)
                    .Take(maximumCount)
                    .ToList();
                foreach (var entry in selected)
                {
                    _pending.Remove(entry.Request.ChannelId);
                    _inFlight.Add(entry.Request.ChannelId);
                }

                SignalIfPending();
                return selected.Select(entry => entry.Request).ToList();
            }
        }

        public void Complete(IReadOnlyCollection<string> channelIds)
        {
            ArgumentNullException.ThrowIfNull(channelIds);
            lock (_sync)
            {
                foreach (var channelId in channelIds)
                {
                    _inFlight.Remove(channelId);
                    if (!_pending.ContainsKey(channelId))
                    {
                        _trackedIds.Remove(channelId);
                    }
                }

                SignalIfPending();
            }
        }

        public void Requeue(IReadOnlyCollection<ChannelRefreshRequest> requests)
        {
            ArgumentNullException.ThrowIfNull(requests);
            lock (_sync)
            {
                foreach (var request in requests)
                {
                    if (request == null || !_trackedIds.Contains(request.ChannelId))
                    {
                        continue;
                    }

                    _inFlight.Remove(request.ChannelId);
                    if (_pending.TryGetValue(request.ChannelId, out var pending))
                    {
                        _pending[request.ChannelId] = pending with
                        {
                            Request = Merge(pending.Request, request)
                        };
                    }
                    else
                    {
                        AddPending(request);
                    }
                }

                SignalIfPending();
            }
        }

        public void Dispose()
        {
            _available.Dispose();
        }

        private void AddPending(ChannelRefreshRequest request)
        {
            var wasEmpty = _pending.Count == 0;
            _pending[request.ChannelId] = new PendingEntry(request, _sequence++);
            if (wasEmpty)
            {
                SignalIfPending();
            }
        }

        private void SignalIfPending()
        {
            if (_pending.Count > 0 && _available.CurrentCount == 0)
            {
                _available.Release();
            }
        }

        private static ChannelRefreshRequest Merge(
            ChannelRefreshRequest current,
            ChannelRefreshRequest incoming)
        {
            var reason = current.Reason < incoming.Reason ? current.Reason : incoming.Reason;
            var staleAfter = current.StaleAfter;
            if (!staleAfter.HasValue || (incoming.StaleAfter.HasValue && incoming.StaleAfter < staleAfter))
            {
                staleAfter = incoming.StaleAfter;
            }

            return reason == current.Reason && staleAfter == current.StaleAfter
                ? current
                : current with { Reason = reason, StaleAfter = staleAfter };
        }
    }
}
