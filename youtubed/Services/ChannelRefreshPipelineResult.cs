namespace youtubed.Services
{
    public sealed class ChannelRefreshPipelineResult
    {
        public int StaleLookaheadCount { get; set; }
        public int SelectedChannelCount { get; set; }
        public int MetadataCallCount { get; set; }
        public int PlaylistCallCount { get; set; }
        public int DurationCallCount { get; set; }
        public int RefreshedChannelCount { get; set; }
        public int UnavailableChannelCount { get; set; }
        public bool CanceledBeforeStartingYoutubeCall { get; set; }
        public bool CanceledDuringYoutubeWork { get; set; }
    }
}
