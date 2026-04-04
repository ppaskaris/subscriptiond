using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using youtubed.Controllers;

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
            Assert.Equal("video-1", Assert.IsType<string>(viewResult.Model));
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
