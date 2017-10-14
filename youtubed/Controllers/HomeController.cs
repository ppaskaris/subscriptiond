using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Controllers
{
    [Route("")]
    public class HomeController : Controller
    {
        private readonly IListService _listService;

        public HomeController(IListService listService)
        {
            _listService = listService;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost, Route("create-list")]
        public async Task<IActionResult> CreateList()
        {
            var list = await _listService.CreateListAsync();
            return RedirectToAction("Index", "List", new
            {
                id = list.Id,
                token = list.TokenString
            });
        }

        [HttpGet, Route("about")]
        public IActionResult About()
        {
            return View();
        }

        [HttpGet, Route("error")]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
