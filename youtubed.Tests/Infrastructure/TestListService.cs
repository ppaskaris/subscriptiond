using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Humanizer;
using youtubed;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    internal sealed class TestListService : IListService
    {
        public static readonly Guid ExistingListId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly byte[] ExistingTokenBytes =
        {
            1, 2, 3, 4, 5, 6, 7, 8,
            9, 10, 11, 12, 13, 14, 15, 16
        };

        public static readonly SubscriptionList ExistingList = new SubscriptionList
        {
            Id = ExistingListId,
            Title = "Existing List",
            PlaybackRate = 2.00m,
            Token = ExistingTokenBytes
        };

        public static readonly SubscriptionList CreatedList = new SubscriptionList
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Title = "Created List",
            PlaybackRate = Constants.DefaultListPlaybackRate,
            Token = new byte[]
            {
                16, 15, 14, 13, 12, 11, 10, 9,
                8, 7, 6, 5, 4, 3, 2, 1
            }
        };

        public Task<SubscriptionList> CreateListAsync(string title)
        {
            return Task.FromResult(new SubscriptionList
            {
                Id = CreatedList.Id,
                Title = title,
                PlaybackRate = Constants.DefaultListPlaybackRate,
                Token = CreatedList.Token
            });
        }

        public Task<SubscriptionList> GetAuthenticatedListAsync(Guid id, string token)
        {
            return Task.FromResult(id == ExistingListId && token == ExistingList.TokenString() ? ExistingList : null);
        }

        public Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token)
        {
            return CreateListView(
                id == ExistingListId && token == ExistingList.TokenString()
                    ? ExistingList
                    : null);
        }

        private static Task<ListViewModel> CreateListView(SubscriptionList list)
        {
            return Task.FromResult(list != null
                ? new ListViewModel
                {
                    Id = list.Id,
                    Title = list.Title,
                    PlaybackRate = list.PlaybackRate,
                    Token = list.TokenString(),
                    Videos = new[]
                    {
                        new VideoViewModel
                        {
                            VideoId = "video-1",
                            VideoTitle = "Test &amp; Video",
                            VideoDuration = TimeSpan.FromMinutes(12).Add(TimeSpan.FromSeconds(34)),
                            VideoThumbnail = "https://example.com/video-1.jpg",
                            ChannelTitle = "Test Channel",
                            ChannelUrl = "https://www.youtube.com/channel/channel-1",
                            VideoPublishedAt = DateTimeOffset.UtcNow.Subtract(5.Minutes())
                        }
                    },
                    Channels = Array.Empty<ChannelModel>(),
                    ExpiredAfter = DateTimeOffset.UtcNow.AddDays(7)
                }
                : null);
        }

        public Task<ListViewModel> GetListChannelViewAsync(SubscriptionList list)
        {
            return Task.FromResult(list != null
                ? new ListViewModel
                {
                    Id = list.Id,
                    Title = list.Title,
                    PlaybackRate = list.PlaybackRate,
                    Token = list.TokenString(),
                    Channels = Array.Empty<ChannelModel>(),
                    ExpiredAfter = DateTimeOffset.UtcNow.AddDays(7)
                }
                : null);
        }

        public Task AddChannelAsync(Guid listId, string channelId) => Task.CompletedTask;

        public Task ForceRefreshAsync(SubscriptionList list) => Task.CompletedTask;

        public Task RemoveChannelAsync(Guid listId, string channelId) => Task.CompletedTask;

        public Task UpdateListAsync(Guid id, string title, decimal playbackRate) => Task.CompletedTask;

        public Task DeleteListAsync(Guid id) => Task.CompletedTask;
    }
}
