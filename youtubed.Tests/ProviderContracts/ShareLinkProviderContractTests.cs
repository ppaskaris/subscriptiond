using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ShareLinkProviderContractTests : ProviderContractTestBase
    {
        protected ShareLinkProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
        }

        protected async Task CreateAndListContractAsync()
        {
            var list = await CreateListAsync();
            var otherList = await CreateListAsync(token: Enumerable.Repeat((byte)2, 40).ToArray());
            var older = await CreateShareLinkAsync(list.Id, "older");
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            var newer = await CreateShareLinkAsync(list.Id, "newer");

            var duplicateCreated = await Provider.ShareLinks.TryCreateAsync(new ShareLink
            {
                Password = older.Password,
                ListId = list.Id,
                CreatedAt = Clock.UtcNow,
                ExpiresAfter = Clock.UtcNow.AddHours(1)
            });
            var crossListDuplicateCreated = await Provider.ShareLinks.TryCreateAsync(new ShareLink
            {
                Password = older.Password,
                ListId = otherList.Id,
                CreatedAt = Clock.UtcNow,
                ExpiresAfter = Clock.UtcNow.AddHours(1)
            });
            var links = await Provider.ShareLinks.GetByListAsync(list.Id);

            Assert.False(duplicateCreated);
            Assert.False(crossListDuplicateCreated);
            Assert.Equal(new[] { newer.Password, older.Password }, links.Select(link => link.Password));
            Assert.All(links, link => Assert.Equal(list.Id, link.ListId));
            Assert.Empty(await Provider.ShareLinks.GetByListAsync(otherList.Id));
        }

        protected async Task ConsumeContractAsync()
        {
            var list = await CreateListAsync(token: Enumerable.Repeat((byte)7, 40).ToArray());
            var link = await CreateShareLinkAsync(list.Id, "consume-once");

            var consumed = await Provider.ShareLinks.ConsumeAsync(link.Password, Clock.UtcNow);
            var consumedAgain = await Provider.ShareLinks.ConsumeAsync(link.Password, Clock.UtcNow.AddSeconds(1));
            var stored = Assert.Single(await Provider.ShareLinks.GetByListAsync(list.Id));

            Assert.NotNull(consumed);
            Assert.Equal(list.Id, consumed.ListId);
            Assert.Equal(list.Token, consumed.Token);
            Assert.Null(consumedAgain);
            Assert.Equal(Clock.UtcNow, stored.UsedAt);

            var expired = await CreateShareLinkAsync(list.Id, "expired");
            Assert.Null(await Provider.ShareLinks.ConsumeAsync(expired.Password, expired.ExpiresAfter));
        }

        protected async Task DeleteContractAsync()
        {
            var firstList = await CreateListAsync();
            var secondList = await CreateListAsync(token: Enumerable.Repeat((byte)2, 40).ToArray());
            await CreateShareLinkAsync(firstList.Id, "delete-one");
            await CreateShareLinkAsync(firstList.Id, "delete-all");
            await CreateShareLinkAsync(secondList.Id, "keep");

            await Provider.ShareLinks.DeleteAsync(firstList.Id, "keep");
            Assert.Equal("keep", Assert.Single(
                await Provider.ShareLinks.GetByListAsync(secondList.Id)).Password);

            await Provider.ShareLinks.DeleteAsync(firstList.Id, "delete-one");
            Assert.Equal("delete-all", Assert.Single(await Provider.ShareLinks.GetByListAsync(firstList.Id)).Password);

            await Provider.ShareLinks.DeleteByListAsync(firstList.Id);
            Assert.Empty(await Provider.ShareLinks.GetByListAsync(firstList.Id));
            Assert.Equal("keep", Assert.Single(await Provider.ShareLinks.GetByListAsync(secondList.Id)).Password);
        }
    }
}
