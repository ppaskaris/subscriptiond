using System;

namespace youtubed.Domain
{
    public class WorkerState
    {
        public DateTimeOffset? NextChannelRefreshAt { get; set; }
        public DateTimeOffset NextPurgeAt { get; set; }
    }
}
