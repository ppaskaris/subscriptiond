using Microsoft.AspNetCore.Mvc;
using youtubed.Models;

namespace youtubed.Controllers
{
    [Route("watch")]
    public class WatchController : Controller
    {
        [HttpGet("{videoId}")]
        public IActionResult Index(string videoId, string title = null, decimal? playbackRate = null)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return NotFound();
            }

            Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            return View(new WatchViewModel
            {
                VideoId = videoId,
                VideoTitle = title,
                PlaybackRate = GetPlaybackRate(playbackRate)
            });
        }

        private static decimal GetPlaybackRate(decimal? playbackRate)
        {
            return playbackRate != null && Constants.IsSupportedPlaybackRate(playbackRate.Value)
                ? playbackRate.Value
                : Constants.DefaultWatchPlaybackRate;
        }
    }
}
