using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ListProviderContractTests : ProviderContractTestBase
    {
        protected ListProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
        }

        protected async Task CreateReadUpdateDeleteContractAsync()
        {
            var list = await CreateListAsync(
                title: "Original",
                playbackRate: 1.25m,
                channelIds: new[] { "UC-z", "UC-a", "UC-z" });

            var created = await Provider.Lists.GetAsync(list.Id);
            Assert.NotNull(created);
            Assert.Equal(list.Token, created.Token);
            Assert.Equal("Original", created.Title);
            Assert.Equal(1.25m, created.PlaybackRate);
            Assert.Equal(list.ExpiredAfter, created.ExpiredAfter);
            Assert.Equal(new[] { "UC-a", "UC-z" }, created.ChannelIds);

            await Provider.Lists.UpdateAsync(list.Id, "Updated", 1.75m);
            var updated = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal("Updated", updated.Title);
            Assert.Equal(1.75m, updated.PlaybackRate);
            Assert.Equal(list.Token, updated.Token);
            Assert.Equal(new[] { "UC-a", "UC-z" }, updated.ChannelIds);

            await Provider.Lists.DeleteAsync(list.Id);
            Assert.Null(await Provider.Lists.GetAsync(list.Id));
        }

        protected async Task AuthenticatedAccessAndDailyRenewalContractAsync()
        {
            var yesterday = Clock.UtcToday.AddDays(-1);
            var list = await CreateListAsync(
                expiredAfter: Clock.UtcNow.AddDays(1),
                expirationRenewedOn: yesterday);
            Clock.RandomDelayValue = TimeSpan.FromDays(45);
            var service = new ListService(
                Provider.Lists,
                Provider.Channels,
                Clock,
                new ChannelRefreshQueue());

            Assert.Null(await service.GetAuthenticatedListAsync(list.Id, "wrong-token"));
            var afterRejectedAccess = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(list.ExpiredAfter, afterRejectedAccess.ExpiredAfter);
            Assert.Equal(yesterday, afterRejectedAccess.ExpirationRenewedOn);

            var token = WebEncoders.Base64UrlEncode(list.Token);
            var firstAccess = await service.GetAuthenticatedListAsync(list.Id, token);
            Assert.NotNull(firstAccess);
            var firstRenewal = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(Clock.UtcNow.AddDays(45), firstRenewal.ExpiredAfter);
            Assert.Equal(Clock.UtcToday, firstRenewal.ExpirationRenewedOn);

            Clock.UtcNow = Clock.UtcNow.AddHours(1);
            Clock.RandomDelayValue = TimeSpan.FromDays(46);
            Assert.NotNull(await service.GetAuthenticatedListAsync(list.Id, token));
            var sameDayAccess = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(firstRenewal.ExpiredAfter, sameDayAccess.ExpiredAfter);

            Clock.UtcNow = Clock.UtcNow.AddDays(1);
            Assert.NotNull(await service.GetAuthenticatedListAsync(list.Id, token));
            var nextDayAccess = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(Clock.UtcNow.AddDays(46), nextDayAccess.ExpiredAfter);
            Assert.Equal(Clock.UtcToday, nextDayAccess.ExpirationRenewedOn);

            var directRenewal = Clock.UtcNow.AddDays(50);
            var ignoredSameDayRenewal = Clock.UtcNow.AddDays(51);
            var followingDay = Clock.UtcToday.AddDays(1);
            var loadedForRenewal = await Provider.Lists.GetAsync(list.Id);
            await Provider.Lists.RenewExpirationAsync(loadedForRenewal, directRenewal, followingDay);
            await Provider.Lists.RenewExpirationAsync(loadedForRenewal, ignoredSameDayRenewal, followingDay);
            var afterRepeatedDirectRenewal = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(directRenewal, afterRepeatedDirectRenewal.ExpiredAfter);
            Assert.Equal(followingDay, afterRepeatedDirectRenewal.ExpirationRenewedOn);
        }

        protected async Task ChannelMembershipContractAsync()
        {
            var list = await CreateListAsync();
            var first = await CreateChannelAsync(title: "Beta");
            var second = await CreateChannelAsync(title: "Alpha");

            await AddChannelToListAsync(list.Id, first.Id);
            await AddChannelToListAsync(list.Id, first.Id);
            await AddChannelToListAsync(list.Id, second.Id);

            var aggregate = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal(
                new[] { first.Id, second.Id }.OrderBy(id => id, StringComparer.Ordinal),
                aggregate.ChannelIds);

            await Provider.Lists.RemoveChannelAsync(list.Id, first.Id);
            await Provider.Lists.RemoveChannelAsync(list.Id, first.Id);
            Assert.Equal(
                new[] { second.Id },
                (await Provider.Lists.GetAsync(list.Id)).ChannelIds);
        }
    }
}
