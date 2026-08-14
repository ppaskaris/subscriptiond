using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlProviderContractSmokeTests : ProviderContractTestBase
    {
        public SqlProviderContractSmokeTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public async Task ProviderHarness_CanCreateAndReadCoreProviderObjects()
        {
            var list = await CreateListAsync(title: "SQL Contract List");
            var channel = await CreateChannelAsync(
                id: "sql-contract-channel",
                title: "SQL Contract Channel");
            await AddChannelToListAsync(list.Id, channel.Id);

            var video = CreateVideo(channel.Id, videoId: "sql-contract-video");
            await SaveVideosAsync(channel, video);
            var shareLink = await CreateShareLinkAsync(list.Id, "sql-contract-share");

            var videoProjection = await Provider.Lists.GetVideoProjectionAsync(
                list,
                Constants.ListRenderMaxItems);
            var shareLinks = await Provider.ShareLinks.GetByListAsync(list.Id);
            var deletedLists = await Provider.ExpirationPurger.PurgeExpiredListsAsync(
                CancellationToken.None);

            Assert.Equal("SQL Contract List", videoProjection.List.Title);
            var projectedChannel = Assert.Single(videoProjection.Channels);
            Assert.Equal("SQL Contract Channel", projectedChannel.Title);
            Assert.Equal("sql-contract-video", Assert.Single(projectedChannel.Videos).VideoId);
            Assert.Equal(shareLink.Password, Assert.Single(shareLinks).Password);
            Assert.Equal(0, deletedLists);
            Assert.Equal("SqlServer", ProviderName);
        }
    }
}
