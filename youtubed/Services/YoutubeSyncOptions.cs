using System;

namespace youtubed.Services
{
    public sealed class YoutubeSyncOptions
    {
        public const string SectionName = "YoutubeSync";

        public int QueueCapacity { get; set; } = Constants.ChannelRefreshQueueCapacity;
        public int CohortSize { get; set; } = Constants.ChannelRefreshBatchSize;
        public TimeSpan CoalescingWindow { get; set; } = TimeSpan.FromMilliseconds(100);
        public int MaximumConcurrentRequests { get; set; } = 4;
        public double RequestsPerSecond { get; set; } = 10;
        public int MaximumPlaylistPages { get; set; } = 4;
        public int MaximumVideosPerChannel { get; set; } = Constants.ListRenderMaxItems;
        public int TransientRetryCount { get; set; } = 2;
        public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
        public int MaximumRetryJitterMilliseconds { get; set; } = 250;
        public TimeSpan TransientCooldown { get; set; } = TimeSpan.FromSeconds(30);
    }
}
