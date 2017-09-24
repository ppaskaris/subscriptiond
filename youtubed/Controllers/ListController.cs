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
        public IActionResult View(Guid? id, string token)
        {
            if (id == null)
            {
                return NotFound();
            }
            if (token == null)
            {
                return BadRequest();
            }

            var list = _listService.GetListById(id.Value);
            if (list == null)
            {
                return NotFound();
            }
            if (token != list.Token)
            {
                return NotFound();
            }

            return View(list);
        }

        [HttpPost]
        public IActionResult Create()
        {
            var list = _listService.CreateList();
            return RedirectToAction("View", new { list.Id, list.Token });
        }
    }
}