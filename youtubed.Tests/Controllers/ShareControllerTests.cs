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
    public class ShareControllerTests
    {
        [Fact]
        public async Task Resolve_BlankPassword_ReturnsNotFound()
        {
            var result = await CreateController().Resolve("");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Resolve_MissingPassword_ReturnsNotFound()
        {
            var shareLinkService = new Mock<IShareLinkService>(MockBehavior.Strict);
            shareLinkService
                .Setup(service => service.ConsumeShareLinkAsync("missing"))
                .ReturnsAsync((ConsumedShareLinkModel)null);

            var result = await CreateController(shareLinkService).Resolve("missing");

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public async Task Resolve_ValidPassword_RedirectsToCanonicalListRoute()
        {
            var listId = Guid.NewGuid();
            var token = new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16
            };
            var expectedToken = new ConsumedShareLinkModel { Token = token }.TokenString;
            var shareLinkService = new Mock<IShareLinkService>(MockBehavior.Strict);
            shareLinkService
                .Setup(service => service.ConsumeShareLinkAsync("amber-forest-river-sky"))
                .ReturnsAsync(new ConsumedShareLinkModel
                {
                    ListId = listId,
                    Token = token
                });

            var result = await CreateController(shareLinkService).Resolve("amber-forest-river-sky");

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Index", redirect.ActionName);
            Assert.Equal("List", redirect.ControllerName);
            Assert.Equal(listId, redirect.RouteValues["id"]);
            Assert.Equal(expectedToken, redirect.RouteValues["token"]);
        }

        private static ShareController CreateController(Mock<IShareLinkService> shareLinkService = null)
        {
            return new ShareController((shareLinkService ?? new Mock<IShareLinkService>(MockBehavior.Strict)).Object);
        }
    }
}
