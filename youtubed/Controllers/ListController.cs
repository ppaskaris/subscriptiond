using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Services;
using youtubed.Models;

namespace youtubed.Controllers
{
    [Route("{token}/list/{id}")]
    public class ListController : Controller
    {
        private readonly IListService _listService;
        private readonly IChannelService _channelService;

        public ListController(
            IListService listService,
            IChannelService channelService)
        {
            _listService = listService;
            _channelService = channelService;
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
            if (token != listView.Token)
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
            if (token != list.TokenString)
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
            if (token != list.TokenString)
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

            return RedirectToAction("Index");
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
            if (token != list.TokenString)
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
            if (token != list.TokenString)
            {
                return NotFound();
            }

            if (list.Title != model.Title)
            {
                await _listService.RenameListAsync(list.Id, model.Title);
            }

            return RedirectToAction("Index");
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
            if (token != list.TokenString)
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
            if (token != list.TokenString)
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
            if (token != list.TokenString)
            {
                return NotFound();
            }

            await _listService.RemoveChannelAsync(list.Id, model.ChannelId);

            return RedirectToAction("Index");
        }
    }
}