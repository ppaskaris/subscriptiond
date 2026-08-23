using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Controllers
{
    [Route("{token}/list/{id}")]
    public partial class ListController : Controller
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

            return RedirectToAction("Index", new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
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
    }
}
