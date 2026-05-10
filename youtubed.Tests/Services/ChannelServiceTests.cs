using System.Threading.Tasks;
using Moq;
using Xunit;
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
                .Setup(value => value.UpdateMetadataAsync(channel.Id, "Updated", "new.png"))
                .Returns(Task.CompletedTask);
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelAsync(channel.Url))
                .ReturnsAsync(new YoutubeChannel
                {
                    Id = channel.Id,
                    Title = "Updated",
                    Thumbnail = "new.png",
                    PlaylistId = channel.PlaylistId
                });

            var service = new ChannelService(repository.Object, youtube.Object, new FakeAppClock());

            await service.RefreshMetadataAsync(channel);

            repository.Verify(value => value.UpdateMetadataAsync(channel.Id, "Updated", "new.png"), Times.Once);
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
                .Setup(value => value.GetChannelAsync(channel.Url))
                .ReturnsAsync(new YoutubeChannel
                {
                    Id = channel.Id,
                    Title = "Same",
                    Thumbnail = "same.png",
                    PlaylistId = channel.PlaylistId
                });

            var service = new ChannelService(repository.Object, youtube.Object, new FakeAppClock());

            await service.RefreshMetadataAsync(channel);

            repository.Verify(value => value.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
            var youtube = new Mock<IYoutubeService>(MockBehavior.Strict);
            youtube
                .Setup(value => value.GetChannelAsync(channel.Url))
                .ReturnsAsync((YoutubeChannel)null);

            var service = new ChannelService(repository.Object, youtube.Object, new FakeAppClock());

            await service.RefreshMetadataAsync(channel);

            repository.Verify(value => value.UpdateMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
