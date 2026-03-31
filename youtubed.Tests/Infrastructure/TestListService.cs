using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Humanizer;
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

        public static readonly ListModel ExistingList = new ListModel
        {
            Id = ExistingListId,
            Title = "Existing List",
            Token = ExistingTokenBytes
        };

        public static readonly ListModel CreatedList = new ListModel
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Title = "Created List",
            Token = new byte[]
            {
                16, 15, 14, 13, 12, 11, 10, 9,
                8, 7, 6, 5, 4, 3, 2, 1
            }
        };

        public Task<ListModel> CreateListAsync(string title)
        {
            return Task.FromResult(new ListModel
            {
                Id = CreatedList.Id,
                Title = title,
                Token = CreatedList.Token
            });
        }

        public Task<ListModel> GetListAsync(Guid id)
        {
            return Task.FromResult(id == ExistingListId ? ExistingList : null);
        }

        public Task<ListViewModel> GetListViewAsync(Guid id)
        {
            return Task.FromResult(id == ExistingListId
                ? new ListViewModel
                {
                    Id = ExistingList.Id,
                    Title = ExistingList.Title,
                    Token = ExistingList.TokenString,
                    Videos = new[]
                    {
                        new VideoViewModel
                        {
                            VideoId = "video-1",
                            VideoTitle = "Test &amp; Video",
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

        public Task AddChannelAsync(Guid listId, string channelId) => Task.CompletedTask;

        public Task RemoveChannelAsync(Guid listId, string channelId) => Task.CompletedTask;

        public Task RenameListAsync(Guid id, string title) => Task.CompletedTask;

        public Task DeleteListAsync(Guid id) => Task.CompletedTask;

        public Task<int> RemoveExpiredListsAsync() => Task.FromResult(0);
    }
}
