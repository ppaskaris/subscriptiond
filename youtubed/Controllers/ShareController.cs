using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Services;

namespace youtubed.Controllers
{
    [Route("share")]
    public class ShareController : Controller
    {
        private readonly IShareLinkService _shareLinkService;

        public ShareController(IShareLinkService shareLinkService)
        {
            _shareLinkService = shareLinkService;
        }

        [HttpGet("{sharePassword}")]
        public async Task<IActionResult> Resolve(string sharePassword)
        {
            if (string.IsNullOrWhiteSpace(sharePassword))
            {
                return NotFound();
            }

            var consumedShareLink = await _shareLinkService.ConsumeShareLinkAsync(sharePassword);
            if (consumedShareLink == null)
            {
                return NotFound();
            }

            return RedirectToAction("Index", "List", new
            {
                id = consumedShareLink.ListId,
                token = consumedShareLink.TokenString
            });
        }
    }
}
