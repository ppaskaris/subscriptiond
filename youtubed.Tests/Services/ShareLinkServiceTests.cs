using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class ShareLinkServiceTests
    {
        private static readonly DateTimeOffset Now =
            new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task ConsumeShareLinkAsync_MissingShare_DoesNotReadListOrMarkUsed()
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            shares.Setup(repository => repository.GetAsync("missing"))
                .ReturnsAsync((ShareLink)null);

            var result = await CreateService(shares, lists).ConsumeShareLinkAsync("missing");

            Assert.Null(result);
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]
        [InlineData(false, -1)]
        public async Task ConsumeShareLinkAsync_UnusableShare_DoesNotReadListOrMarkUsed(
            bool alreadyUsed,
            int expiryMinutes)
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            link.UsedAt = alreadyUsed ? Now.AddMinutes(-1) : null;
            link.ExpiresAfter = Now.AddMinutes(expiryMinutes);
            shares.Setup(repository => repository.GetAsync(link.Password)).ReturnsAsync(link);

            var result = await CreateService(shares, lists)
                .ConsumeShareLinkAsync(link.Password);

            Assert.Null(result);
        }

        [Fact]
        public async Task ConsumeShareLinkAsync_MissingList_DoesNotMarkShareUsed()
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            shares.Setup(repository => repository.GetAsync(link.Password)).ReturnsAsync(link);
            lists.Setup(repository => repository.GetAsync(link.ListId))
                .ReturnsAsync((SubscriptionList)null);

            var result = await CreateService(shares, lists)
                .ConsumeShareLinkAsync(link.Password);

            Assert.Null(result);
            shares.Verify(
                repository => repository.TryMarkUsedAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<DateTimeOffset>()),
                Times.Never);
        }

        [Fact]
        public async Task ConsumeShareLinkAsync_ConditionalWriteFailure_RevealsNoToken()
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            var list = CreateList(link.ListId);
            shares.Setup(repository => repository.GetAsync(link.Password)).ReturnsAsync(link);
            lists.Setup(repository => repository.GetAsync(link.ListId)).ReturnsAsync(list);
            shares.Setup(repository => repository.TryMarkUsedAsync(
                    link.Password,
                    link.ListId,
                    Now))
                .ReturnsAsync(false);

            var result = await CreateService(shares, lists)
                .ConsumeShareLinkAsync(link.Password);

            Assert.Null(result);
        }

        [Fact]
        public async Task ConsumeShareLinkAsync_ReturnsTokenOnlyAfterConditionalWriteSucceeds()
        {
            var calls = new List<string>();
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            var list = CreateList(link.ListId);
            shares.Setup(repository => repository.GetAsync(link.Password))
                .Callback(() => calls.Add("read-share"))
                .ReturnsAsync(link);
            lists.Setup(repository => repository.GetAsync(link.ListId))
                .Callback(() => calls.Add("read-list"))
                .ReturnsAsync(list);
            shares.Setup(repository => repository.TryMarkUsedAsync(
                    link.Password,
                    link.ListId,
                    Now))
                .Callback(() => calls.Add("mark-used"))
                .ReturnsAsync(true);

            var result = await CreateService(shares, lists)
                .ConsumeShareLinkAsync(link.Password);

            Assert.Equal(new[] { "read-share", "read-list", "mark-used" }, calls);
            Assert.Equal(list.Id, result.ListId);
            Assert.Equal(list.Token, result.Token);
            Assert.NotSame(list.Token, result.Token);
        }

        [Fact]
        public async Task ConsumeShareLinkAsync_WriteException_DoesNotCreateAResult()
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            var list = CreateList(link.ListId);
            shares.Setup(repository => repository.GetAsync(link.Password)).ReturnsAsync(link);
            lists.Setup(repository => repository.GetAsync(link.ListId)).ReturnsAsync(list);
            shares.Setup(repository => repository.TryMarkUsedAsync(
                    link.Password,
                    link.ListId,
                    Now))
                .ThrowsAsync(new InvalidOperationException("Share-link update failed."));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService(shares, lists).ConsumeShareLinkAsync(link.Password));

            Assert.Equal("Share-link update failed.", exception.Message);
            Assert.DoesNotContain(link.Password, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                Convert.ToBase64String(list.Token),
                exception.ToString(),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ConsumeShareLinkAsync_RestartAfterCommittedWriteCannotRevealToken()
        {
            var shares = new Mock<IShareLinkRepository>(MockBehavior.Strict);
            var lists = new Mock<IListRepository>(MockBehavior.Strict);
            var link = CreateShareLink();
            var list = CreateList(link.ListId);
            shares.Setup(repository => repository.GetAsync(link.Password))
                .ReturnsAsync(() => link);
            lists.Setup(repository => repository.GetAsync(link.ListId)).ReturnsAsync(list);
            shares.Setup(repository => repository.TryMarkUsedAsync(
                    link.Password,
                    link.ListId,
                    Now))
                .Callback(() => link.UsedAt = Now)
                .ThrowsAsync(new InvalidOperationException("Response was interrupted."));
            var service = CreateService(shares, lists);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ConsumeShareLinkAsync(link.Password));
            var restartedResult = await service.ConsumeShareLinkAsync(link.Password);

            Assert.Null(restartedResult);
            lists.Verify(repository => repository.GetAsync(link.ListId), Times.Once);
            shares.Verify(repository => repository.TryMarkUsedAsync(
                link.Password,
                link.ListId,
                Now), Times.Once);
        }

        private static ShareLinkService CreateService(
            Mock<IShareLinkRepository> shares,
            Mock<IListRepository> lists)
        {
            return new ShareLinkService(
                shares.Object,
                lists.Object,
                new FakeAppClock { UtcNow = Now });
        }

        private static ShareLink CreateShareLink()
        {
            return new ShareLink
            {
                Password = "amber-forest-river-sky",
                ListId = Guid.NewGuid(),
                CreatedAt = Now.AddMinutes(-5),
                ExpiresAfter = Now.AddHours(1)
            };
        }

        private static SubscriptionList CreateList(Guid id)
        {
            return new SubscriptionList
            {
                Id = id,
                Token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray(),
                Title = "Shared list",
                ExpiredAfter = Now.AddDays(45)
            };
        }
    }
}
