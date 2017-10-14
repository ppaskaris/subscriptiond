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

        [HttpGet, Route("create-list")]
        public IActionResult CreateList()
        {
            return View(new CreateListModel());
        }

        [HttpPost, Route("create-list")]
        public async Task<IActionResult> CreateList(CreateListModel model)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            var list = await _listService.CreateListAsync(model.Title);
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

        [HttpGet, Route("error/{statusCode?}")]
        public IActionResult Error(int? statusCode)
        {
            return View(new ErrorViewModel
            {
                StatusCode = statusCode ?? 500,
                RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
            });
        }
    }
}
