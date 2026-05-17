using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public class ChannelServiceTests
    {
        [Fact]
        public async Task RefreshMetadataAsync_UpdatesRepositoryWhenMetadataChanges()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/channel/channel-1",
                Title = "Original",
                Thumbnail = "old.png",
                PlaylistId = "playlist-1"
            };
            var repository = new Mock<IChannelRepository>(MockBehavior.Strict);
            repository
                .Setup(value => value.UpdateMetadataAsync(channel.Id, "https://www.youtube.com/channel/channel-1", "Updated", "new.png", "playlist-updated"))
                .Returns(Task.CompletedTask);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelByIdAsync(channel.Id))
                .ReturnsAsync(new YoutubeChannel
                {
                    Id = channel.Id,
                    Title = "Updated",
                    Thumbnail = "new.png",
                    PlaylistId = "playlist-updated"
                });

            var service = CreateService(repository.Object, youtube.Object);

            var refreshed = await service.RefreshMetadataAsync(channel);

            Assert.NotNull(refreshed);
            Assert.Equal(channel.Id, refreshed.Id);
            Assert.Equal("https://www.youtube.com/channel/channel-1", refreshed.Url);
            Assert.Equal("Updated", refreshed.Title);
            Assert.Equal("new.png", refreshed.Thumbnail);
            Assert.Equal("playlist-updated", refreshed.PlaylistId);
            Assert.Equal(ChannelStatus.Active, refreshed.Status);
            Assert.Equal(ChannelStatusReason.None, refreshed.StatusReason);
            Assert.Null(refreshed.StatusUpdatedAt);
            repository.Verify(value => value.UpdateMetadataAsync(channel.Id, "https://www.youtube.com/channel/channel-1", "Updated", "new.png", "playlist-updated"), Times.Once);
        }

        [Fact]
        public async Task RefreshMetadataAsync_UnchangedMetadataSkipsRepositoryUpdate()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/channel/channel-1",
                Title = "Same",
                Thumbnail = "same.png",
                PlaylistId = "playlist-1"
            };
            var repository = new Mock<IChannelRepository>(MockBehavior.Strict);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelByIdAsync(channel.Id))
                .ReturnsAsync(new YoutubeChannel
                {
                    Id = channel.Id,
                    Title = "Same",
                    Thumbnail = "same.png",
                    PlaylistId = channel.PlaylistId
                });

            var service = CreateService(repository.Object, youtube.Object);

            var refreshed = await service.RefreshMetadataAsync(channel);

            Assert.Same(channel, refreshed);
            repository.Verify(value => value.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RefreshMetadataAsync_LegacyUrlUsesCanonicalChannelId()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/user/legacy-name",
                Title = "Original",
                Thumbnail = "old.png",
                PlaylistId = "playlist-1"
            };
            var canonicalUrl = "https://www.youtube.com/channel/channel-1";
            var repository = new Mock<IChannelRepository>(MockBehavior.Strict);
            repository
                .Setup(value => value.UpdateMetadataAsync(channel.Id, canonicalUrl, "Updated", "new.png", "playlist-updated"))
                .Returns(Task.CompletedTask);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelByIdAsync(channel.Id))
                .ReturnsAsync(new YoutubeChannel
                {
                    Id = channel.Id,
                    Title = "Updated",
                    Thumbnail = "new.png",
                    PlaylistId = "playlist-updated"
                });

            var service = CreateService(repository.Object, youtube.Object);

            var refreshed = await service.RefreshMetadataAsync(channel);

            Assert.NotNull(refreshed);
            Assert.Equal(canonicalUrl, refreshed.Url);
            repository.Verify(value => value.MarkUnavailableAsync(It.IsAny<string>(), It.IsAny<ChannelStatusReason>(), It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>()), Times.Never);
            youtube.Verify(value => value.GetChannelByUrlAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RefreshMetadataAsync_MissingChannelIdDoesNotFallBackToStoredUrl()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/user/legacy-name",
                Title = "Original",
                Thumbnail = "old.png",
                PlaylistId = "playlist-1"
            };
            var repository = new Mock<IChannelRepository>(MockBehavior.Strict);
            var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
            repository
                .Setup(value => value.MarkUnavailableAsync(
                    channel.Id,
                    ChannelStatusReason.NotFound,
                    now,
                    now.Add(Constants.ChannelUnavailableStaleDelay)))
                .Returns(Task.CompletedTask);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelByIdAsync(channel.Id))
                .ReturnsAsync((YoutubeChannel)null);

            var service = CreateService(repository.Object, youtube.Object, new FakeAppClock { UtcNow = now });

            var refreshed = await service.RefreshMetadataAsync(channel);

            Assert.Null(refreshed);
            youtube.Verify(value => value.GetChannelByUrlAsync(It.IsAny<string>()), Times.Never);
            repository.Verify(value => value.MarkUnavailableAsync(
                channel.Id,
                ChannelStatusReason.NotFound,
                now,
                now.Add(Constants.ChannelUnavailableStaleDelay)), Times.Once);
        }

        [Fact]
        public async Task RefreshMetadataAsync_MissingYoutubeChannelSkipsRepositoryUpdate()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/channel/channel-1",
                Title = "Same",
                Thumbnail = "same.png",
                PlaylistId = "playlist-1"
            };
            var repository = new Mock<IChannelRepository>(MockBehavior.Strict);
            var now = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
            repository
                .Setup(value => value.MarkUnavailableAsync(
                    channel.Id,
                    ChannelStatusReason.NotFound,
                    now,
                    now.Add(Constants.ChannelUnavailableStaleDelay)))
                .Returns(Task.CompletedTask);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelByIdAsync(channel.Id))
                .ReturnsAsync((YoutubeChannel)null);

            var service = CreateService(repository.Object, youtube.Object, new FakeAppClock { UtcNow = now });

            var refreshed = await service.RefreshMetadataAsync(channel);

            Assert.Null(refreshed);
            repository.Verify(value => value.MarkUnavailableAsync(
                channel.Id,
                ChannelStatusReason.NotFound,
                now,
                now.Add(Constants.ChannelUnavailableStaleDelay)), Times.Once);
            repository.Verify(value => value.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task UpdateChannelHostedService_SkipsVideoRefreshWhenMetadataRefreshMarksUnavailable()
        {
            var channel = new StaleChannelModel
            {
                Id = "channel-1",
                Url = "https://www.youtube.com/channel/channel-1",
                Title = "Missing",
                Thumbnail = "missing.png",
                PlaylistId = "playlist-1"
            };
            using var cancellationTokenSource = new System.Threading.CancellationTokenSource();
            var channelService = new Mock<IChannelService>(MockBehavior.Strict);
            channelService
                .SetupSequence(value => value.GetNextStaleChannelOrDefaultAsync())
                .ReturnsAsync(channel)
                .ReturnsAsync((StaleChannelModel)null);
            channelService
                .Setup(value => value.RefreshMetadataAsync(channel))
                .Callback(() => cancellationTokenSource.Cancel())
                .ReturnsAsync((StaleChannelModel)null);
            var channelVideoService = new Mock<IChannelVideoService>(MockBehavior.Strict);
            var service = new TestableUpdateChannelHostedService(
                channelService.Object,
                channelVideoService.Object,
                new FakeAppClock(),
                Mock.Of<ILogger<UpdateChannelHostedService>>());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(cancellationTokenSource.Token));

            channelVideoService.Verify(value => value.RefreshVideosAsync(It.IsAny<StaleChannelModel>()), Times.Never);
        }

        private sealed class TestableUpdateChannelHostedService : UpdateChannelHostedService
        {
            public TestableUpdateChannelHostedService(
                IChannelService channelService,
                IChannelVideoService channelVideoService,
                IAppClock clock,
                ILogger<UpdateChannelHostedService> logger)
                : base(channelService, channelVideoService, clock, logger)
            {
            }

            public Task RunAsync(System.Threading.CancellationToken cancellationToken)
            {
                return ExecuteAsync(cancellationToken);
            }
        }

        private static ChannelService CreateService(
            IChannelRepository repository,
            IYoutubeService youtube,
            FakeAppClock clock = null)
        {
            clock ??= new FakeAppClock();
            return new ChannelService(
                repository,
                youtube,
                clock,
                new ChannelUrlLookupCache());
        }
    }
}
