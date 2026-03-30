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
    }
}
