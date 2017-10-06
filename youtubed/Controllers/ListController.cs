using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Services;
using youtubed.Models;

namespace youtubed.Controllers
{
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
        public async Task<IActionResult> View(Guid? id, string token)
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

        [HttpPost]
        public async Task<IActionResult> Create()
        {
            var list = await _listService.CreateListAsync();
            return RedirectToAction("View", new
            {
                Id = list.Id,
                Token = list.TokenString
            });
        }

        [HttpGet]
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

            return View(new AddChannelModel
            {
                Id = list.Id,
                Token = list.TokenString
            });
        }

        [HttpPost]
        public async Task<IActionResult> AddChannel(AddChannelModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var list = await _listService.GetListAsync(model.Id.Value);
            if (list == null)
            {
                return BadRequest();
            }
            if (model.Token != list.TokenString)
            {
                return BadRequest();
            }

            var channel = await _channelService.GetOrCreateChannel(model.Url);
            if (channel == null)
            {
                ModelState.AddModelError("Url", "Cannot find channel on YouTube.");
                return View(model);
            }

            await _listService.AddChannelAsync(list.Id, channel.Id);

            return RedirectToAction("View", new
            {
                Id = list.Id,
                Token = list.TokenString
            });
        }
    }
}