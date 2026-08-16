namespace youtubed.Services
{
    public enum ChannelRefreshDisposition
    {
        Refreshed,
        Unavailable,
        RetryTransient,
        SkippedSuperseded,
        FailedPermanent
    }

    public sealed record ChannelRefreshOutcome(
        string ChannelId,
        ChannelRefreshDisposition Disposition,
        int PlaylistCalls,
        int DurationCalls);
}
