namespace youtubed.Domain
{
    public class ChannelRefreshResult
    {
        public Channel Channel { get; set; }
        public bool VideosRefreshed { get; set; }
        public System.DateTimeOffset? EarliestPublishedAt { get; set; }
    }
}
