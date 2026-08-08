using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Cosmos;
using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosAuthenticatedListPageIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;
        private readonly ITestOutputHelper _output;

        public CosmosAuthenticatedListPageIntegrationTests(
            CosmosTestFixture fixture,
            ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [CosmosFact]
        public async Task AuthenticatedListPage_UsesBoundedRenewalAndSingleSameDayPointRead()
        {
            var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var listId = Guid.NewGuid();
            var token = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            var document = new CosmosListDocument
            {
                Id = listId.ToString("D"),
                Token = token,
                Title = "Measured list page",
                PlaybackRate = 1.25m,
                ExpiredAfter = now.AddDays(1),
                ExpirationRenewedOn = today.AddDays(-1),
                Ttl = (int)TimeSpan.FromDays(1).TotalSeconds,
                Channels = new[]
                {
                    new CosmosProjectedChannelDocument
                    {
                        Id = "measured-channel",
                        Url = "https://www.youtube.com/channel/measured-channel",
                        Title = "Measured Channel",
                        Thumbnail = "channel.png",
                        StaleAfter = now.AddHours(1),
                        Status = ChannelStatus.Active.ToString(),
                        StatusReason = ChannelStatusReason.None.ToString(),
                        Videos = new[]
                        {
                            new CosmosVideoDocument
                            {
                                Id = "measured-video",
                                Title = "Measured Video",
                                DurationTicks = TimeSpan.FromMinutes(5).Ticks,
                                PublishedAt = now.AddMinutes(-10),
                                Thumbnail = "video.png"
                            }
                        }
                    }
                }
            };
            var lists = _fixture.GetContainer(CosmosTestFixture.ListsContainerName);
            await lists.CreateItemAsync(document, new PartitionKey(document.Id));
            var repository = new CosmosListRepository(
                lists,
                _fixture.GetContainer(CosmosTestFixture.ChannelsContainerName),
                new FakeAppClock { UtcNow = now });
            int renewalRequests;
            double renewalCharge;
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                var projection = await repository.GetAuthenticatedVideoProjectionAsync(
                    listId,
                    token,
                    now.AddDays(45),
                    today,
                    101);
                Assert.Equal("measured-video", Assert.Single(
                    Assert.Single(projection.Channels).Videos).VideoId);
                renewalRequests = scope.RequestCount;
                renewalCharge = scope.RequestCharge;
            }

            int sameDayRequests;
            double sameDayCharge;
            using (var scope = CosmosRequestChargeScope.Begin())
            {
                Assert.NotNull(await repository.GetAuthenticatedVideoProjectionAsync(
                    listId,
                    token,
                    now.AddDays(46),
                    today,
                    101));
                sameDayRequests = scope.RequestCount;
                sameDayCharge = scope.RequestCharge;
            }

            _output.WriteLine(
                $"Renewal page: {renewalRequests} requests, {renewalCharge:F2} RU; " +
                $"same-day page: {sameDayRequests} request, {sameDayCharge:F2} RU.");
            CosmosReleaseBudgets.AssertWithin(
                CosmosReleaseBudgets.Operations["list_page_renewal"],
                renewalRequests,
                renewalCharge);
            CosmosReleaseBudgets.AssertWithin(
                CosmosReleaseBudgets.Operations["list_page"],
                sameDayRequests,
                sameDayCharge);
        }
    }
}
