using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;

namespace youtubed.Controllers
{
    [Route("{token}/list/{id}")]
    public class ListController : Controller
    {
        private readonly IListService _listService;
        private readonly IChannelService _channelService;
        private readonly IShareLinkService _shareLinkService;
        private readonly IAppClock _clock;

        public ListController(
            IListService listService,
            IChannelService channelService,
            IShareLinkService shareLinkService,
            IAppClock clock)
        {
            _listService = listService;
            _channelService = channelService;
            _shareLinkService = shareLinkService;
            _clock = clock;
        }

        [HttpGet]
        public async Task<IActionResult> Index(Guid? id, string token)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var listView = await _listService.GetAuthenticatedListViewAsync(id.Value, token);
            if (listView == null)
            {
                return NotFound();
            }

            return View(listView);
        }

        [HttpGet, Route("channels")]
        public async Task<IActionResult> Channels(Guid? id, string token)
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

            var listView = await _listService.GetListChannelViewAsync(list);
            return View(listView);
        }

        [HttpGet, Route("add-channel")]
        public async Task<IActionResult> AddChannel(Guid? id, string token)
        {
            if (id == null)
            {
                return NotFound();
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

            return View(new AddChannelModel());
        }

        [HttpPost, Route("add-channel")]
        public async Task<IActionResult> AddChannel(
            Guid? id, string token, AddChannelModel model)
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
                return BadRequest();
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var channel = await _channelService.GetOrCreateChannelAsync(model.Url);
            if (channel == null)
            {
                ModelState.AddModelError("Url", "Cannot find channel on YouTube.");
                return View(model);
            }

            try
            {
                await _listService.AddChannelAsync(list.Id, channel.Id);
            }
            catch (ListCapacityExceededException exception)
            {
                ModelState.AddModelError(string.Empty, exception.Message);
                return View(model);
            }

            return RedirectToAction(nameof(Channels), new { token = list.TokenString, id = list.Id });
        }

        [HttpGet, Route("edit")]
        public async Task<IActionResult> Edit(Guid? id, string token)
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

            return View(new EditListModel
            {
                Title = list.Title,
                PlaybackRate = list.PlaybackRate
            });
        }

        [HttpPost, Route("edit")]
        public async Task<IActionResult> Edit(Guid? id, string token, EditListModel model)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            if (list.Title != model.Title || list.PlaybackRate != model.PlaybackRate)
            {
                await _listService.UpdateListAsync(list.Id, model.Title, model.PlaybackRate);
            }

            return RedirectToAction("Index", new { token = list.TokenString, id = list.Id });
        }

        [HttpGet, Route("delete")]
        public async Task<IActionResult> Delete(Guid? id, string token)
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

            return View(list);
        }

        [HttpPost, Route("delete")]
        public async Task<IActionResult> Delete(Guid? id, string token, DeleteListModel model)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            await _listService.DeleteListAsync(list.Id);

            return RedirectToAction("Index", "Home");
        }

        [HttpPost, Route("remove-channel")]
        public async Task<IActionResult> RemoveChannel(Guid? id, string token, RemoveChannelModel model)
        {
            if (id == null)
            {
                return BadRequest();
            }
            if (token == null)
            {
                return BadRequest();
            }

            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            await _listService.RemoveChannelAsync(list.Id, model.ChannelId);

            return RedirectToAction(nameof(Channels), new { token = list.TokenString, id = list.Id });
        }

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
                Token = list.TokenString,
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

            return RedirectToAction(nameof(Share), new { token = list.TokenString, id = list.Id });
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

            return RedirectToAction(nameof(Share), new { token = list.TokenString, id = list.Id });
        }

        [HttpPost, Route("share/delete")]
        public async Task<IActionResult> DeleteShareLink(Guid? id, string token, string password)
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

            return RedirectToAction(nameof(Share), new { token = list.TokenString, id = list.Id });
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
