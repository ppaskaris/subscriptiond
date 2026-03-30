using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using youtubed.Controllers;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Tests.Controllers
{
    public class ListControllerTests
    {
        [Fact]
        public async Task Index_MissingId_ReturnsBadRequest()
        {
            var controller = CreateController();

            var result = await controller.Index(null, "token");

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task Index_MissingToken_ReturnsBadRequest()
        {
            var controller = CreateController();

            var result = await controller.Index(Guid.NewGuid(), null);

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task Index_ListMissing_ReturnsNotFound()
        {
            var id = Guid.NewGuid();
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService.Setup(service => service.GetListViewAsync(id)).ReturnsAsync((ListViewModel)null);

            var result = await CreateController(listService: listService).Index(id, "token");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Index_TokenMismatch_ReturnsNotFound()
        {
            var id = Guid.NewGuid();
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService
                .Setup(service => service.GetListViewAsync(id))
                .ReturnsAsync(new ListViewModel { Id = id, Token = "expected" });

            var result = await CreateController(listService: listService).Index(id, "wrong");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Index_ValidRequest_ReturnsViewWithStaleCount()
        {
            var id = Guid.NewGuid();
            var model = new ListViewModel
            {
                Id = id,
                Token = "expected",
                Title = "My List",
                StaleCount = 3,
                Videos = new[] { new VideoViewModel { VideoId = "video-1", VideoTitle = "Video" } }
            };
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService.Setup(service => service.GetListViewAsync(id)).ReturnsAsync(model);

            var result = await CreateController(listService: listService).Index(id, "expected");

            var viewResult = Assert.IsType<ViewResult>(result);
            var returnedModel = Assert.IsType<ListViewModel>(viewResult.Model);
            Assert.Same(model, returnedModel);
            Assert.Equal(3, returnedModel.StaleCount);
        }

        [Fact]
        public async Task AddChannelGet_MissingId_ReturnsNotFound()
        {
            var result = await CreateController().AddChannel(null, "token");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task AddChannelGet_MissingToken_ReturnsBadRequest()
        {
            var result = await CreateController().AddChannel(Guid.NewGuid(), null);

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task AddChannelGet_MissingList_ReturnsNotFound()
        {
            var id = Guid.NewGuid();
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);

            var result = await CreateController(listService: listService).AddChannel(id, "token");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task AddChannelGet_TokenMismatch_ReturnsNotFound()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);

            var result = await CreateController(listService: listService).AddChannel(id, "wrong");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task AddChannelGet_ValidRequest_ReturnsViewWithModel()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);

            var result = await CreateController(listService: listService).AddChannel(id, list.TokenString);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.IsType<AddChannelModel>(viewResult.Model);
        }

        [Fact]
        public async Task AddChannelPost_MissingId_ReturnsBadRequest()
        {
            var result = await CreateController().AddChannel(null, "token", new AddChannelModel());

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task AddChannelPost_MissingToken_ReturnsBadRequest()
        {
            var result = await CreateController().AddChannel(Guid.NewGuid(), null, new AddChannelModel());

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task AddChannelPost_MissingList_ReturnsBadRequest()
        {
            var id = Guid.NewGuid();
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);

            var result = await CreateController(listService: listService)
                .AddChannel(id, "token", new AddChannelModel());

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task AddChannelPost_TokenMismatch_ReturnsBadRequest()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);

            var result = await CreateController(listService: listService)
                .AddChannel(id, "wrong", new AddChannelModel { Url = "https://www.youtube.com/channel/channel-1" });

            Assert.IsType<BadRequestResult>(result);
        }

        [Fact]
        public async Task AddChannelPost_InvalidModelState_ReturnsSameView()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);
            var controller = CreateController(listService: listService);
            var model = new AddChannelModel();
            controller.ModelState.AddModelError("Url", "Required");

            var result = await controller.AddChannel(id, list.TokenString, model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
        }

        [Fact]
        public async Task AddChannelPost_ChannelLookupFails_AddsModelErrorAndReturnsView()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var model = new AddChannelModel { Url = "https://www.youtube.com/channel/channel-1" };
            var listService = CreateListServiceReturning(id, list);
            var channelService = new Mock<IChannelService>(MockBehavior.Strict);
            channelService.Setup(service => service.GetOrCreateChannelAsync(model.Url)).ReturnsAsync((ChannelModel)null);

            var controller = CreateController(listService, channelService);

            var result = await controller.AddChannel(id, list.TokenString, model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
            Assert.Equal("Cannot find channel on YouTube.", controller.ModelState["Url"].Errors[0].ErrorMessage);
            listService.Verify(service => service.AddChannelAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task AddChannelPost_ValidRequest_AddsChannelAndRedirects()
        {
            var id = Guid.NewGuid();
            var list = CreateList(id);
            var model = new AddChannelModel { Url = "https://www.youtube.com/channel/channel-1" };
            var channel = new ChannelModel { Id = "channel-1" };
            var listService = CreateListServiceReturning(id, list);
            listService.Setup(service => service.AddChannelAsync(id, channel.Id)).Returns(Task.CompletedTask);
            var channelService = new Mock<IChannelService>(MockBehavior.Strict);
            channelService.Setup(service => service.GetOrCreateChannelAsync(model.Url)).ReturnsAsync(channel);

            var result = await CreateController(listService, channelService).AddChannel(id, list.TokenString, model);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Null(redirect.ControllerName);
            Assert.Equal(list.TokenString, redirect.RouteValues["token"]);
            Assert.Equal(id, redirect.RouteValues["id"]);
            listService.Verify(service => service.AddChannelAsync(id, channel.Id), Times.Once);
        }

        [Fact]
        public async Task EditGet_GuardsAndSuccess()
        {
            Assert.IsType<BadRequestResult>(await CreateController().Edit(null, "token"));
            Assert.IsType<BadRequestResult>(await CreateController().Edit(Guid.NewGuid(), null));

            var id = Guid.NewGuid();
            var missingListService = new Mock<IListService>(MockBehavior.Strict);
            missingListService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);
            Assert.IsType<NotFoundResult>(await CreateController(listService: missingListService).Edit(id, "token"));

            var list = CreateList(id, "Original");
            var listService = CreateListServiceReturning(id, list);
            Assert.IsType<NotFoundResult>(await CreateController(listService: listService).Edit(id, "wrong"));

            var successResult = await CreateController(listService: listService).Edit(id, list.TokenString);
            var viewResult = Assert.IsType<ViewResult>(successResult);
            Assert.Equal("Original", Assert.IsType<EditListModel>(viewResult.Model).Title);
        }

        [Fact]
        public async Task EditPost_InvalidModelState_ReturnsSameView()
        {
            var controller = CreateController();
            var model = new EditListModel();
            controller.ModelState.AddModelError("Title", "Required");

            var result = await controller.Edit(Guid.NewGuid(), "token", model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
        }

        [Fact]
        public async Task EditPost_GuardsRenameAndNoRename()
        {
            Assert.IsType<BadRequestResult>(await CreateController().Edit(null, "token", new EditListModel()));
            Assert.IsType<BadRequestResult>(await CreateController().Edit(Guid.NewGuid(), null, new EditListModel()));

            var id = Guid.NewGuid();
            var missingListService = new Mock<IListService>(MockBehavior.Strict);
            missingListService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);
            Assert.IsType<NotFoundResult>(await CreateController(listService: missingListService)
                .Edit(id, "token", new EditListModel { Title = "Updated" }));

            var list = CreateList(id, "Original");
            var listService = CreateListServiceReturning(id, list);
            Assert.IsType<NotFoundResult>(await CreateController(listService: listService)
                .Edit(id, "wrong", new EditListModel { Title = "Updated" }));

            listService.Setup(service => service.RenameListAsync(id, "Updated")).Returns(Task.CompletedTask);
            var renameResult = await CreateController(listService: listService)
                .Edit(id, list.TokenString, new EditListModel { Title = "Updated" });
            var renameRedirect = Assert.IsType<RedirectToActionResult>(renameResult);
            Assert.Equal("Index", renameRedirect.ActionName);
            Assert.Equal(list.TokenString, renameRedirect.RouteValues["token"]);
            Assert.Equal(id, renameRedirect.RouteValues["id"]);
            listService.Verify(service => service.RenameListAsync(id, "Updated"), Times.Once);

            var sameTitleResult = await CreateController(listService: listService)
                .Edit(id, list.TokenString, new EditListModel { Title = "Original" });
            var sameTitleRedirect = Assert.IsType<RedirectToActionResult>(sameTitleResult);
            Assert.Equal("Index", sameTitleRedirect.ActionName);
            Assert.Equal(list.TokenString, sameTitleRedirect.RouteValues["token"]);
            Assert.Equal(id, sameTitleRedirect.RouteValues["id"]);
            listService.Verify(service => service.RenameListAsync(It.IsAny<Guid>(), "Original"), Times.Never);
        }

        [Fact]
        public async Task DeleteGet_GuardsAndSuccess()
        {
            Assert.IsType<BadRequestResult>(await CreateController().Delete(null, "token"));
            Assert.IsType<BadRequestResult>(await CreateController().Delete(Guid.NewGuid(), null));

            var id = Guid.NewGuid();
            var missingListService = new Mock<IListService>(MockBehavior.Strict);
            missingListService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);
            Assert.IsType<NotFoundResult>(await CreateController(listService: missingListService).Delete(id, "token"));

            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);
            Assert.IsType<NotFoundResult>(await CreateController(listService: listService).Delete(id, "wrong"));

            var result = await CreateController(listService: listService).Delete(id, list.TokenString);
            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(list, viewResult.Model);
        }

        [Fact]
        public async Task DeletePost_InvalidModelState_ReturnsSameView()
        {
            var controller = CreateController();
            var model = new DeleteListModel();
            controller.ModelState.AddModelError("Confirm", "Required");

            var result = await controller.Delete(Guid.NewGuid(), "token", model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
        }

        [Fact]
        public async Task DeletePost_GuardsAndSuccess()
        {
            Assert.IsType<BadRequestResult>(await CreateController().Delete(null, "token", new DeleteListModel()));
            Assert.IsType<BadRequestResult>(await CreateController().Delete(Guid.NewGuid(), null, new DeleteListModel()));

            var id = Guid.NewGuid();
            var missingListService = new Mock<IListService>(MockBehavior.Strict);
            missingListService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);
            Assert.IsType<NotFoundResult>(await CreateController(listService: missingListService)
                .Delete(id, "token", new DeleteListModel { Confirm = true }));

            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);
            Assert.IsType<NotFoundResult>(await CreateController(listService: listService)
                .Delete(id, "wrong", new DeleteListModel { Confirm = true }));

            listService.Setup(service => service.DeleteListAsync(id)).Returns(Task.CompletedTask);
            var result = await CreateController(listService: listService)
                .Delete(id, list.TokenString, new DeleteListModel { Confirm = true });
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Equal("Home", redirect.ControllerName);
            listService.Verify(service => service.DeleteListAsync(id), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelPost_InvalidModelState_ReturnsSameView()
        {
            var controller = CreateController();
            var model = new RemoveChannelModel();
            controller.ModelState.AddModelError("ChannelId", "Required");

            var result = await controller.RemoveChannel(Guid.NewGuid(), "token", model);

            var viewResult = Assert.IsType<ViewResult>(result);
            Assert.Same(model, viewResult.Model);
        }

        [Fact]
        public async Task RemoveChannelPost_GuardsAndSuccess()
        {
            Assert.IsType<BadRequestResult>(await CreateController().RemoveChannel(null, "token", new RemoveChannelModel()));
            Assert.IsType<BadRequestResult>(await CreateController().RemoveChannel(Guid.NewGuid(), null, new RemoveChannelModel()));

            var id = Guid.NewGuid();
            var missingListService = new Mock<IListService>(MockBehavior.Strict);
            missingListService.Setup(service => service.GetListAsync(id)).ReturnsAsync((ListModel)null);
            Assert.IsType<NotFoundResult>(await CreateController(listService: missingListService)
                .RemoveChannel(id, "token", new RemoveChannelModel { ChannelId = "channel-1" }));

            var list = CreateList(id);
            var listService = CreateListServiceReturning(id, list);
            Assert.IsType<NotFoundResult>(await CreateController(listService: listService)
                .RemoveChannel(id, "wrong", new RemoveChannelModel { ChannelId = "channel-1" }));

            listService.Setup(service => service.RemoveChannelAsync(id, "channel-1")).Returns(Task.CompletedTask);
            var result = await CreateController(listService: listService)
                .RemoveChannel(id, list.TokenString, new RemoveChannelModel { ChannelId = "channel-1" });
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Equal(list.TokenString, redirect.RouteValues["token"]);
            Assert.Equal(id, redirect.RouteValues["id"]);
            listService.Verify(service => service.RemoveChannelAsync(id, "channel-1"), Times.Once);
        }

        private static ListController CreateController(
            Mock<IListService> listService = null,
            Mock<IChannelService> channelService = null)
        {
            return new ListController(
                (listService ?? new Mock<IListService>(MockBehavior.Strict)).Object,
                (channelService ?? new Mock<IChannelService>(MockBehavior.Strict)).Object);
        }

        private static Mock<IListService> CreateListServiceReturning(Guid id, ListModel list)
        {
            var listService = new Mock<IListService>(MockBehavior.Strict);
            listService.Setup(service => service.GetListAsync(id)).ReturnsAsync(list);
            return listService;
        }

        private static ListModel CreateList(Guid id, string title = "My List")
        {
            return new ListModel
            {
                Id = id,
                Title = title,
                Token = new byte[]
                {
                    1, 2, 3, 4, 5, 6, 7, 8,
                    9, 10, 11, 12, 13, 14, 15, 16
                }
            };
        }
    }
}
