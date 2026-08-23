using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Models;

namespace youtubed.Controllers
{
    public partial class ListController
    {
        [HttpGet, Route("share")]
        public async Task<IActionResult> Share(Guid? id, string token)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            var shareLinks = await _shareLinkService.GetShareLinksAsync(list.Id);
            var now = _clock.UtcNow;

            return View(new ShareListViewModel
            {
                ListId = list.Id,
                Token = ListRouteToken.Encode(list),
                Title = list.Title,
                ShareLinks = shareLinks.Select(shareLink => new ShareLinkListItemViewModel
                {
                    Password = shareLink.Password,
                    ShareUrl = CreateShareUrl(shareLink.Password),
                    ExpiresAfter = shareLink.ExpiresAfter,
                    UsedAt = shareLink.UsedAt,
                    Status = GetStatus(shareLink, now)
                })
            });
        }

        [HttpPost, Route("share/create")]
        public async Task<IActionResult> CreateShareLink(Guid? id, string token)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            await _shareLinkService.CreateShareLinkAsync(list.Id);

            return RedirectToAction(nameof(Share), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
        }

        [HttpPost, Route("share/delete-all")]
        public async Task<IActionResult> DeleteAllShareLinks(Guid? id, string token)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            await _shareLinkService.DeleteShareLinksAsync(list.Id);

            return RedirectToAction(nameof(Share), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
        }

        [HttpPost, Route("share/delete")]
        public async Task<IActionResult> DeleteShareLink(
            Guid? id,
            string token,
            string password)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                return BadRequest();
            }

            await _shareLinkService.DeleteShareLinkInListAsync(list.Id, password);

            return RedirectToAction(nameof(Share), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
        }

        private static string GetStatus(ShareLinkModel shareLink, DateTimeOffset now)
        {
            if (shareLink.UsedAt != null)
            {
                return "Used";
            }

            return shareLink.ExpiresAfter <= now
                ? "Expired"
                : "Active";
        }

        private string CreateShareUrl(string password)
        {
            if (Url == null || HttpContext?.Request == null || !Request.Host.HasValue)
            {
                return $"/share/{password}";
            }

            return Url.Action(
                "Resolve",
                "Share",
                new { sharePassword = password },
                Request.Scheme,
                Request.Host.Value);
        }
    }
}
