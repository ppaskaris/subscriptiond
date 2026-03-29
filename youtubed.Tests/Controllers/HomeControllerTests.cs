using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using youtubed.Controllers;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Tests.Controllers
{
    public class HomeControllerTests
    {
        [Fact]
        public void CreateListGet_ReturnsViewWithFreshModel()
        {
            var controller = CreateController();

            var result = controller.CreateList();

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<CreateListModel>(viewResult.Model);
            Assert.Equal("My List", model.Title);
        }

        [Fact]
        public async Task CreateListPost_InvalidModelState_ReturnsSameView()
        {
            var listService = new Mock<IListService>(MockBehavior.Strict);
            var controller = CreateController(listService);
            var model = new CreateListModel();
            controller.ModelState.AddModelError("Title", "Required");

            var result = await controller.CreateList(model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
            listService.Verify(
                service => service.CreateListAsync(It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task CreateListPost_ValidModel_CreatesListAndRedirectsToSecretRoute()
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = new byte[]
                {
                    1, 2, 3, 4, 5, 6, 7, 8,
                    9, 10, 11, 12, 13, 14, 15, 16
                },
                Title = "My List"
            };

            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService
                .Setup(service => service.CreateListAsync("My List"))
                .ReturnsAsync(list);

            var controller = CreateController(listService);
            var model = new CreateListModel { Title = "My List" };

            var result = await controller.CreateList(model);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Equal("List", redirect.ControllerName);
            Assert.Equal(list.Id, redirect.RouteValues["id"]);
            Assert.Equal(list.TokenString, redirect.RouteValues["token"]);
            listService.Verify(service => service.CreateListAsync("My List"), Times.Once);
        }

        private static HomeController CreateController(Mock<IListService> listService = null)
        {
            return new HomeController((listService ?? new Mock<IListService>(MockBehavior.Strict)).Object);
        }
    }
}
