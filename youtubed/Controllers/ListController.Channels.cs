using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Controllers
{
    public partial class ListController
    {
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

        [HttpGet, Route("refresh")]
        public async Task<IActionResult> Refresh(Guid? id, string token)
        {
            if (id == null || token == null)
            {
                return BadRequest();
            }

            var list = await _listService.GetAuthenticatedListAsync(id.Value, token);
            if (list == null)
            {
                return NotFound();
            }

            await _listService.ForceRefreshAsync(list);
            return RedirectToAction(nameof(Index), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
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
            Guid? id,
            string token,
            AddChannelModel model)
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

            return RedirectToAction(nameof(Channels), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
        }

        [HttpPost, Route("remove-channel")]
        public async Task<IActionResult> RemoveChannel(
            Guid? id,
            string token,
            RemoveChannelModel model)
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

            return RedirectToAction(nameof(Channels), new
            {
                token = ListRouteToken.Encode(list),
                id = list.Id
            });
        }
    }
}
