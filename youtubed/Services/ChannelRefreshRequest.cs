using System;

namespace youtubed.Services
{
    public enum ChannelRefreshReason
    {
        Missing = 0,
        Forced = 1,
        Stale = 2
    }

    public sealed record ChannelRefreshRequest(
        string ChannelId,
        ChannelRefreshReason Reason,
        DateTimeOffset? StaleAfter = null);
}
