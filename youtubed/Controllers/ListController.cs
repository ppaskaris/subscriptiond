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

            return View(list);
        }

        [HttpPost]
        public async Task<IActionResult> Create()
        {
            var list = await _listService.CreateListAsync();
            return RedirectToAction("View", new { id = list.Id, token = list.TokenString });
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

            var channel = await _channelService.CreateChannelAsync(model.Url);
            if (channel == null)
            {
                ModelState.AddModelError("Url", "Cannot find channel on YouTube.");
                return View(model);
            }

            return View("ChannelInfo", channel);
        }
    }
}