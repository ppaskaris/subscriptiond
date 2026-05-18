using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Moq;
using Xunit;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Services
{
    public sealed class ListServiceTests
    {
        [Fact]
        public async Task GetListViewAsync_MapsVideoProjectionWithStableNowStaleCountAndVideoCap()
        {
            var id = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
            var list = new SubscriptionList
            {
                Id = id,
                Token = token,
                Title = "Projection List",
                PlaybackRate = 1.50m,
                ExpiredAfter = now.Add(Constants.ListMaxAgeMin)
            };
            var staleChannel = new ListVideoProjection.Channel
            {
                Id = "channel-stale",
                Url = "https://www.youtube.com/channel/channel-stale",
                Title = "Stale",
                Thumbnail = "stale.png",
                StaleAfter = now.AddMinutes(-1),
                Videos = CreateVideos("channel-stale", now).ToList()
            };
            var freshChannel = new ListVideoProjection.Channel
            {
                Id = "channel-fresh",
                Url = "https://www.youtube.com/channel/channel-fresh",
                Title = "Fresh",
                Thumbnail = "fresh.png",
                StaleAfter = now.AddMinutes(1)
            };
            var unavailableChannel = new ListVideoProjection.Channel
            {
                Id = "channel-unavailable",
                Url = "https://www.youtube.com/channel/channel-unavailable",
                Title = "Unavailable",
                Thumbnail = "unavailable.png",
                StaleAfter = now.AddMinutes(-5),
                Status = ChannelStatus.Unavailable,
                StatusReason = ChannelStatusReason.NotFound
            };
            var repository = new Mock<IListRepository>(MockBehavior.Strict);
            repository
                .Setup(value => value.GetVideoProjectionAsync(
                    id,
                    now.Add(Constants.ListMaxAgeMin),
                    Constants.ListRenderMaxItems + 1))
                .ReturnsAsync(new ListVideoProjection
                {
                    List = list,
                    Channels = new[]
                    {
                        staleChannel,
                        freshChannel,
                        unavailableChannel
                    }
                });
            var service = new ListService(repository.Object, new FakeAppClock { UtcNow = now });

            var view = await service.GetListViewAsync(id);

            Assert.Equal(id, view.Id);
            Assert.Equal(WebEncoders.Base64UrlEncode(token), view.Token);
            Assert.Equal(now, view.Now);
            Assert.Equal(Constants.ListMaxAgeMin, view.MaxAge);
            Assert.Equal(1, view.StaleCount);
            Assert.True(view.HasMoreVideos);
            Assert.Equal(Constants.ListRenderMaxItems, view.Videos.Count());
            Assert.Equal(new[] { "video-a", "video-b" }, view.Videos.Take(2).Select(video => video.VideoId).ToArray());
            Assert.Equal("Stale", view.Videos.First().ChannelTitle);
            Assert.Equal(new[] { "Stale", "Fresh", "Unavailable" }, view.Channels.Select(channel => channel.Title).ToArray());
        }

        [Fact]
        public async Task GetListChannelViewAsync_MapsChannelProjectionWithoutVideos()
        {
            var id = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
            var list = new SubscriptionList
            {
                Id = id,
                Token = Enumerable.Repeat((byte)7, 40).ToArray(),
                Title = "Channels",
                ExpiredAfter = now.Add(Constants.ListMaxAgeMin)
            };
            var repository = new Mock<IListRepository>(MockBehavior.Strict);
            repository
                .Setup(value => value.GetChannelProjectionAsync(id, now.Add(Constants.ListMaxAgeMin)))
                .ReturnsAsync(new ListChannelProjection
                {
                    List = list,
                    Channels = new[]
                    {
                        new ListChannelProjection.Channel
                        {
                            Id = "channel-1",
                            Url = "https://www.youtube.com/channel/channel-1",
                            Title = "Channel",
                            Thumbnail = "channel.png",
                            StaleAfter = now.AddMinutes(-1)
                        }
                    }
                });
            var service = new ListService(repository.Object, new FakeAppClock { UtcNow = now });

            var view = await service.GetListChannelViewAsync(id);

            Assert.Equal("Channels", view.Title);
            Assert.Empty(view.Videos);
            var channel = Assert.Single(view.Channels);
            Assert.Equal("channel-1", channel.Id);
            Assert.Equal(now.AddMinutes(-1), channel.StaleAfter);
        }

        private static IEnumerable<ChannelVideo> CreateVideos(string channelId, DateTimeOffset now)
        {
            yield return new ChannelVideo
            {
                ChannelId = channelId,
                VideoId = "video-b",
                Title = "B",
                Duration = TimeSpan.FromMinutes(5),
                PublishedAt = now.AddMinutes(-1),
                ThumbnailUrl = "b.png"
            };
            yield return new ChannelVideo
            {
                ChannelId = channelId,
                VideoId = "video-a",
                Title = "A",
                Duration = TimeSpan.FromMinutes(5),
                PublishedAt = now.AddMinutes(-1),
                ThumbnailUrl = "a.png"
            };

            for (var index = 0; index < Constants.ListRenderMaxItems; index++)
            {
                yield return new ChannelVideo
                {
                    ChannelId = channelId,
                    VideoId = $"video-{index:D3}",
                    Title = $"Video {index}",
                    Duration = TimeSpan.FromMinutes(5),
                    PublishedAt = now.AddMinutes(-2 - index),
                    ThumbnailUrl = $"{index}.png"
                };
            }
        }
    }
}
