using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ExpirationPurgerProviderContractTests : ProviderContractTestBase
    {
        private readonly ExpirationPurgeBehavior _purgeBehavior;

        protected ExpirationPurgerProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
            _purgeBehavior = fixture.PurgeBehavior;
        }

        protected async Task ExpiredListCleanupContractAsync()
        {
            var expired = await CreateListAsync(
                id: Guid.Parse("10000000-0000-0000-0000-000000000000"),
                expiredAfter: Clock.UtcNow.AddDays(1));
            var active = await CreateListAsync(
                id: Guid.Parse("20000000-0000-0000-0000-000000000000"),
                expiredAfter: Clock.UtcNow.AddDays(1).AddTicks(1),
                token: Enumerable.Repeat((byte)2, 40).ToArray());
            var channel = await CreateChannelAsync("expired-list-channel");
            await AddChannelToListAsync(expired.Id, channel.Id);
            await CreateShareLinkAsync(expired.Id, "expired-list-link");
            Clock.UtcNow = Clock.UtcNow.AddDays(1);

            var removed = await Provider.ExpirationPurger.PurgeExpiredListsAsync(CancellationToken.None);

            if (_purgeBehavior == ExpirationPurgeBehavior.NoOp)
            {
                Assert.Equal(0, removed);
                Assert.NotNull(await Provider.Lists.GetAsync(expired.Id));
                Assert.NotNull(await Provider.Lists.GetAsync(active.Id));
                Assert.Equal(
                    "expired-list-link",
                    Assert.Single(await Provider.ShareLinks.GetByListAsync(expired.Id)).Password);
                var subscribedChannel = Assert.Single(await Provider.Channels.GetBatchAsync(
                    new[] { channel.Id },
                    CancellationToken.None));
                Assert.Equal(expired.Id, Assert.Single(subscribedChannel.SubscribedListIds));
                Assert.Equal(1, subscribedChannel.SubscriptionCount);
                return;
            }

            Assert.Equal(1, removed);
            Assert.Null(await Provider.Lists.GetAsync(expired.Id));
            Assert.NotNull(await Provider.Lists.GetAsync(active.Id));
            Assert.Empty(await Provider.ShareLinks.GetByListAsync(expired.Id));
            var orphanedChannel = Assert.Single(await Provider.Channels.GetBatchAsync(
                new[] { channel.Id },
                CancellationToken.None));
            Assert.Empty(orphanedChannel.SubscribedListIds);
            Assert.Equal(0, orphanedChannel.SubscriptionCount);
        }

        protected async Task ExpiredShareLinkCleanupContractAsync()
        {
            var list = await CreateListAsync();
            var initialNow = Clock.UtcNow;
            var delete = new ShareLink
            {
                Password = "delete-link",
                ListId = list.Id,
                CreatedAt = initialNow,
                ExpiresAfter = initialNow
            };
            var keep = new ShareLink
            {
                Password = "keep-link",
                ListId = list.Id,
                CreatedAt = initialNow.AddTicks(1),
                ExpiresAfter = initialNow.AddTicks(1)
            };
            Assert.True(await Provider.ShareLinks.TryCreateAsync(delete));
            Assert.True(await Provider.ShareLinks.TryCreateAsync(keep));
            Clock.UtcNow = initialNow
                .Add(Constants.ShareLinkRetentionAfterExpiration);

            var removed = await Provider.ExpirationPurger.PurgeExpiredShareLinksAsync(
                CancellationToken.None);

            if (_purgeBehavior == ExpirationPurgeBehavior.NoOp)
            {
                Assert.Equal(0, removed);
                Assert.Equal(
                    new[] { keep.Password, delete.Password },
                    (await Provider.ShareLinks.GetByListAsync(list.Id))
                        .Select(link => link.Password));
                return;
            }

            var remaining = await Provider.ShareLinks.GetByListAsync(list.Id);

            Assert.Equal(1, removed);
            Assert.Equal(keep.Password, Assert.Single(remaining).Password);
        }

        protected async Task ExpiredChannelCleanupContractAsync()
        {
            var list = await CreateListAsync();
            var orphan = await CreateChannelAsync("orphan-channel");
            var attached = await CreateChannelAsync("attached-channel");
            await AddChannelToListAsync(list.Id, attached.Id);
            await SaveVideosAsync(orphan, CreateVideo(orphan.Id, "orphan-video"));

            var removed = await Provider.ExpirationPurger.PurgeExpiredChannelsAsync(
                CancellationToken.None);

            if (_purgeBehavior == ExpirationPurgeBehavior.NoOp)
            {
                Assert.Equal(0, removed);
                var unchanged = await Provider.Channels.GetBatchAsync(
                    new[] { orphan.Id, attached.Id },
                    CancellationToken.None);
                Assert.Equal(new[] { orphan.Id, attached.Id }, unchanged.Select(channel => channel.Id));
                Assert.Equal(
                    "orphan-video",
                    Assert.Single(unchanged.Single(channel => channel.Id == orphan.Id).Videos).VideoId);
                var unchangedProjection = await Provider.Lists.GetChannelProjectionAsync(
                list);
                Assert.Equal(attached.Id, Assert.Single(unchangedProjection.Channels).Id);
                return;
            }

            var remaining = await Provider.Channels.GetBatchAsync(
                new[] { orphan.Id, attached.Id },
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(attached.Id, Assert.Single(remaining).Id);
            var projection = await Provider.Lists.GetChannelProjectionAsync(list);
            Assert.Equal(attached.Id, Assert.Single(projection.Channels).Id);
        }
    }
}
