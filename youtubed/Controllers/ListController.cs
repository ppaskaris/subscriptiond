using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Services;

namespace youtubed.Controllers
{
    public class ListController : Controller
    {
        private readonly IListService _listService;

        public ListController(IListService listService)
        {
            _listService = listService;
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
            return RedirectToAction("View", new { list.Id, Token = list.TokenString });
        }
    }
}