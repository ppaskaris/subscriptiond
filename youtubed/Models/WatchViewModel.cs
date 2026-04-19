namespace youtubed.Models
{
    public class WatchViewModel
    {
        public string VideoId { get; set; }
        public string VideoTitle { get; set; }
        public decimal PlaybackRate { get; set; } = Constants.DefaultWatchPlaybackRate;
    }
}
