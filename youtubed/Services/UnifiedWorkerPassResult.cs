using System;
using youtubed.Persistence;

namespace youtubed.Services
{
    internal sealed class UnifiedWorkerPassResult
    {
        public bool PurgeRan { get; set; }
        public int ExpiredListDeleteCount { get; set; }
        public int ExpiredShareLinkDeleteCount { get; set; }
        public int ExpiredChannelDeleteCount { get; set; }
        public ChannelRefreshPipelineResult ChannelRefresh { get; set; }
        public DateTimeOffset? NextChannelRefreshAt { get; set; }
        public DateTimeOffset NextPurgeAt { get; set; }
        public ConsistencyRecoveryPassResult ConsistencyRecovery { get; set; }
        public DateTimeOffset NextConsistencyRecoveryAt { get; set; }
    }
}
