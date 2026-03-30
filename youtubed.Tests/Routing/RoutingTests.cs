using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Routing
{
    public class RoutingTests : IClassFixture<RoutingWebApplicationFactory>
    {
        private readonly RoutingWebApplicationFactory _factory;

        public RoutingTests(RoutingWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Theory]
        [InlineData("/")]
        [InlineData("/create-list")]
        [InlineData("/about")]
        [InlineData("/error/404")]
        public async Task PublicGetRoutes_ReturnSuccess(string path)
        {
            using var client = _factory.CreateClient();

            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task UnknownRoute_RedirectsToErrorPage()
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            var response = await client.GetAsync("/missing-page");

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/error/404", response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task CreateList_PostRedirectsToSecretListRoute()
        {
            using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            using var response = await client.PostAsync(
                "/create-list",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("Title", "Created List")
                }));

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(
                $"/{TestListService.CreatedList.TokenString}/list/{TestListService.CreatedList.Id}",
                response.Headers.Location?.OriginalString);
        }

        [Fact]
        public async Task AddChannelRoute_RemainsReachable()
        {
            using var client = _factory.CreateClient();

            var response = await client.GetAsync(
                $"/{TestListService.ExistingList.TokenString}/list/{TestListService.ExistingListId}/add-channel");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task HomePage_UsesCdnBootstrapAndSelfHostedSiteCss()
        {
            using var client = _factory.CreateClient();

            var content = await client.GetStringAsync("/");

            Assert.Contains("https://cdnjs.cloudflare.com/ajax/libs/twitter-bootstrap/3.3.7/css/bootstrap.min.css", content);
            Assert.Contains("/css/site.css?v=", content);
        }

        [Fact]
        public async Task ListPage_RendersExplicitSecretRouteLinks()
        {
            using var client = _factory.CreateClient();

            var content = await client.GetStringAsync(
                $"/{TestListService.ExistingList.TokenString}/list/{TestListService.ExistingListId}");

            var expectedBasePath =
                $"/{TestListService.ExistingList.TokenString}/list/{TestListService.ExistingListId}";

            Assert.Contains($"href=\"{expectedBasePath}\"", content);
            Assert.Contains($"href=\"{expectedBasePath}/add-channel\"", content);
            Assert.Contains($"href=\"{expectedBasePath}/edit\"", content);
            Assert.Contains($"href=\"{expectedBasePath}/delete\"", content);
        }

        [Theory]
        [MemberData(nameof(FormPagePaths))]
        public async Task FormPages_RenderValidationScriptsFromCdn(string path)
        {
            using var client = _factory.CreateClient();

            var content = await client.GetStringAsync(path);

            Assert.Contains("https://cdn.jsdelivr.net/npm/jquery@3.7.1/dist/jquery.min.js", content);
            Assert.Contains("integrity=\"sha384-1H217gwSVyLSIfaLxHbE7dRb3v4mYCKbpQvzx0cegeju1MVsGrX5xXxAvs/HgeFs\"", content);
            Assert.Contains("https://cdn.jsdelivr.net/npm/jquery-validation@1.22.1/dist/jquery.validate.min.js", content);
            Assert.Contains("integrity=\"sha384-DIFfDxcYkhbAXYdxOYFZshXsis24zK4HtbU7qI30u9/eP7JtiRIGuOaLsoYL5QTs\"", content);
            Assert.Contains("https://cdn.jsdelivr.net/npm/jquery-validation-unobtrusive@4.0.0/dist/jquery.validate.unobtrusive.min.js", content);
            Assert.Contains("integrity=\"sha384-DU2a51mTHKDhpXhTyJQ++hP8L9L8Gc48TlvbzBmUof71V7kNVs4ELmaVJKPxcAGn\"", content);
        }

        public static IEnumerable<object[]> FormPagePaths()
        {
            yield return new object[] { "/create-list" };
            yield return new object[]
            {
                $"/{TestListService.ExistingList.TokenString}/list/{TestListService.ExistingListId}/add-channel"
            };
            yield return new object[]
            {
                $"/{TestListService.ExistingList.TokenString}/list/{TestListService.ExistingListId}/edit"
            };
        }
    }
}
