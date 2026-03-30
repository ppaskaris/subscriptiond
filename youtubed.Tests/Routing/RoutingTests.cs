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

            Assert.Contains("https://ajax.aspnetcdn.com/ajax/jquery/jquery-2.2.0.min.js", content);
            Assert.Contains("integrity=\"sha384-K+ctZQ+LL8q6tP7I94W+qzQsfRV2a+AfHIi9k8z8l9ggpc8X+Ytst4yBo/hH+8Fk\"", content);
            Assert.Contains("https://ajax.aspnetcdn.com/ajax/jquery.validate/1.14.0/jquery.validate.min.js", content);
            Assert.Contains("integrity=\"sha384-Fnqn3nxp3506LP/7Y3j/25BlWeA3PXTyT1l78LjECcPaKCV12TsZP7yyMxOe/G/k\"", content);
            Assert.Contains("https://ajax.aspnetcdn.com/ajax/jquery.validation.unobtrusive/3.2.6/jquery.validate.unobtrusive.min.js", content);
            Assert.Contains("integrity=\"sha384-JrXK+k53HACyavUKOsL+NkmSesD2P+73eDMrbTtTk0h4RmOF8hF8apPlkp26JlyH\"", content);
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
