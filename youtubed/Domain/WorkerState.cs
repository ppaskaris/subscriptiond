using System;

namespace youtubed.Domain
{
    public class WorkerState
    {
        public DateTimeOffset? NextChannelRefreshAt { get; set; }
        public DateTimeOffset NextPurgeAt { get; set; }

        public bool IsChannelRefreshDue(DateTimeOffset now)
        {
            return NextChannelRefreshAt.HasValue
                && NextChannelRefreshAt.Value <= now;
        }

        public bool IsPurgeDue(DateTimeOffset now)
        {
            return NextPurgeAt <= now;
        }
    }
}
