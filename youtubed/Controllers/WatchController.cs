using Microsoft.AspNetCore.Mvc;

namespace youtubed.Controllers
{
    [Route("watch")]
    public class WatchController : Controller
    {
        [HttpGet("{videoId}")]
        public IActionResult Index(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return NotFound();
            }

            Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            return View(model: videoId);
        }
    }
}
