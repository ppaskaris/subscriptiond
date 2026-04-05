using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using youtubed.Controllers;
using youtubed.Models;

namespace youtubed.Tests.Controllers
{
    public class WatchControllerTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Index_BlankVideoId_ReturnsNotFound(string videoId)
        {
            var result = CreateController().Index(videoId);

            Assert.IsType<NotFoundResult>(result);
        }

        [Fact]
        public void Index_ValidVideoId_SetsReferrerPolicyAndReturnsView()
        {
            var controller = CreateController();

            var result = controller.Index("video-1");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<WatchViewModel>(viewResult.Model);
            Assert.Equal("video-1", model.VideoId);
            Assert.Null(model.VideoTitle);
            Assert.Equal("strict-origin-when-cross-origin", controller.Response.Headers["Referrer-Policy"].ToString());
        }

        [Fact]
        public void Index_WithTitle_PassesTitleIntoViewModel()
        {
            var controller = CreateController();

            var result = controller.Index("video-1", "Test &amp; Video");

            var viewResult = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<WatchViewModel>(viewResult.Model);
            Assert.Equal("video-1", model.VideoId);
            Assert.Equal("Test &amp; Video", model.VideoTitle);
            Assert.Equal("strict-origin-when-cross-origin", controller.Response.Headers["Referrer-Policy"].ToString());
        }

        private static WatchController CreateController()
        {
            return new WatchController
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }
    }
}
