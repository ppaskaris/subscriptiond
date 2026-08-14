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
            var list = await CreateListAsync(title: "Original", playbackRate: 1.25m);

            var created = await Provider.Lists.GetAsync(list.Id);
            Assert.NotNull(created);
            Assert.Equal(list.Token, created.Token);
            Assert.Equal("Original", created.Title);
            Assert.Equal(1.25m, created.PlaybackRate);
            Assert.Equal(list.ExpiredAfter, created.ExpiredAfter);

            await Provider.Lists.UpdateAsync(list.Id, "Updated", 1.75m);
            var updated = await Provider.Lists.GetAsync(list.Id);
            Assert.Equal("Updated", updated.Title);
            Assert.Equal(1.75m, updated.PlaybackRate);
            Assert.Equal(list.Token, updated.Token);

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
            var service = new ListService(Provider.Lists, Clock, new ChannelRefreshQueue());

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
            await Provider.Lists.RenewExpirationAsync(list.Id, directRenewal, followingDay);
            await Provider.Lists.RenewExpirationAsync(list.Id, ignoredSameDayRenewal, followingDay);
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

            var added = await Provider.Lists.GetChannelProjectionAsync(list);
            Assert.Equal(new[] { second.Id, first.Id }, added.Channels.Select(channel => channel.Id));

            await Provider.Lists.RemoveChannelAsync(list.Id, first.Id);
            await Provider.Lists.RemoveChannelAsync(list.Id, first.Id);
            var removed = await Provider.Lists.GetChannelProjectionAsync(list);
            Assert.Equal(second.Id, Assert.Single(removed.Channels).Id);
        }

        protected async Task ChannelAndVideoReadModelsContractAsync()
        {
            var list = await CreateListAsync(title: "Projected List", playbackRate: 1.5m);
            var withVideos = await CreateChannelAsync(title: "With Videos");
            var alsoWithVideos = await CreateChannelAsync(title: "Also With Videos");
            var empty = await CreateChannelAsync(title: "Empty");
            await AddChannelToListAsync(list.Id, withVideos.Id);
            await AddChannelToListAsync(list.Id, alsoWithVideos.Id);
            await AddChannelToListAsync(list.Id, empty.Id);
            await SaveVideosAsync(
                withVideos,
                CreateVideo(withVideos.Id, "older", publishedAt: Clock.UtcNow.AddHours(-2)),
                CreateVideo(withVideos.Id, "newest", publishedAt: Clock.UtcNow.AddMinutes(-30)));
            await SaveVideosAsync(
                alsoWithVideos,
                CreateVideo(alsoWithVideos.Id, "middle", publishedAt: Clock.UtcNow.AddHours(-1)));

            var channelProjection = await Provider.Lists.GetChannelProjectionAsync(list);
            Assert.Equal(list.Id, channelProjection.List.Id);
            Assert.Equal(
                new[] { alsoWithVideos.Id, empty.Id, withVideos.Id },
                channelProjection.Channels.Select(channel => channel.Id));

            var videoProjection = await Provider.Lists.GetVideoProjectionAsync(list, 2);
            Assert.Equal("Projected List", videoProjection.List.Title);
            Assert.Equal(1.5m, videoProjection.List.PlaybackRate);
            Assert.Equal(3, videoProjection.Channels.Count);
            Assert.Empty(videoProjection.Channels.Single(channel => channel.Id == empty.Id).Videos);
            var projectedVideoIds = videoProjection.Channels
                .SelectMany(channel => channel.Videos)
                .Select(video => video.VideoId)
                .ToArray();
            Assert.Equal(2, projectedVideoIds.Length);
            Assert.Contains("newest", projectedVideoIds);
            Assert.Contains("middle", projectedVideoIds);
            Assert.DoesNotContain("older", projectedVideoIds);
        }
    }
}
