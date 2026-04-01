using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Services;
using youtubed.Models;
using youtubed.SecurityTheatre;

namespace youtubed.Controllers
{
    [Route("{token}/list/{id}")]
    public class ListController : Controller
    {
        private readonly IListService _listService;
        private readonly IChannelService _channelService;
        private readonly IShareLinkService _shareLinkService;

        public ListController(
            IListService listService,
            IChannelService channelService,
            IShareLinkService shareLinkService)
        {
            _listService = listService;
            _channelService = channelService;
            _shareLinkService = shareLinkService;
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

            var listView = await _listService.GetListViewAsync(id.Value);
            if (listView == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, listView.Token))
            {
                return NotFound();
            }

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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return BadRequest();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            await _listService.AddChannelAsync(list.Id, channel.Id);

            return RedirectToAction("Index", new { token = list.TokenString, id = list.Id });
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
            {
                return NotFound();
            }

            return View(new EditListModel
            {
                Title = list.Title
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
            {
                return NotFound();
            }

            if (list.Title != model.Title)
            {
                await _listService.RenameListAsync(list.Id, model.Title);
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
            {
                return NotFound();
            }

            await _listService.RemoveChannelAsync(list.Id, model.ChannelId);

            return RedirectToAction("Index", new { token = list.TokenString, id = list.Id });
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
            {
                return NotFound();
            }

            var shareLinks = await _shareLinkService.GetShareLinksAsync(list.Id);
            var now = DateTimeOffset.Now;

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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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

            var list = await _listService.GetListAsync(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (TokenUtils.NotEqual(token, list.TokenString))
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
