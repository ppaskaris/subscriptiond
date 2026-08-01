using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.WebUtilities;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.SecurityTheatre;

namespace youtubed.Services
{
    public class ListService : IListService
    {
        private readonly IListRepository _listRepository;
        private readonly IAppClock _clock;
        private readonly IWorkerStateStore _workerStateStore;
        private readonly IWorkerWakeSignal _wakeSignal;

        public ListService(
            IListRepository listRepository,
            IAppClock clock,
            IWorkerStateStore workerStateStore = null,
            IWorkerWakeSignal wakeSignal = null)
        {
            _listRepository = listRepository;
            _clock = clock;
            _workerStateStore = workerStateStore;
            _wakeSignal = wakeSignal;
        }

        public async Task<ListModel> CreateListAsync(string title)
        {
            var now = _clock.UtcNow;
            var list = new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = CreateToken(),
                Title = title,
                PlaybackRate = Constants.DefaultListPlaybackRate,
                ExpiredAfter = CreateExpiredAfter(now)
            };

            await _listRepository.CreateAsync(list);
            return ToListModel(list);
        }

        public async Task<ListModel> GetListAsync(Guid id)
        {
            return ToListModel(await _listRepository.GetAsync(id));
        }

        public async Task<ListModel> GetAuthenticatedListAsync(Guid id, string token)
        {
            var list = await GetListAsync(id);
            if (list == null || TokenUtils.NotEqual(DecodeToken(token), list.Token))
            {
                return null;
            }

            var today = _clock.UtcToday;
            if (list.ExpirationRenewedOn != today)
            {
                await _listRepository.RenewExpirationAsync(
                    id,
                    CreateExpiredAfter(_clock.UtcNow),
                    today);
            }

            return list;
        }

        public async Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token)
        {
            var decodedToken = DecodeToken(token);
            if (decodedToken == null)
            {
                return null;
            }

            var now = _clock.UtcNow;
            var projection = await _listRepository.GetAuthenticatedVideoProjectionAsync(
                id,
                decodedToken,
                CreateExpiredAfter(now),
                DateOnly.FromDateTime(now.UtcDateTime),
                Constants.ListRenderMaxItems + 1);
            return CreateListView(projection, now);
        }

        public async Task<ListViewModel> GetListViewAsync(Guid id)
        {
            var list = await GetListAsync(id);
            return await GetListViewAsync(list);
        }

        public Task<ListViewModel> GetListViewAsync(ListModel list)
        {
            return GetListViewCoreAsync(list);
        }

        public async Task<ListViewModel> GetListChannelViewAsync(Guid id)
        {
            var list = await GetListAsync(id);
            return await GetListChannelViewAsync(list);
        }

        public Task<ListViewModel> GetListChannelViewAsync(ListModel list)
        {
            return GetListChannelViewCoreAsync(list);
        }

        public async Task AddChannelAsync(Guid listId, string channelId)
        {
            await _listRepository.AddChannelAsync(listId, channelId);
            if (_workerStateStore != null)
            {
                await _workerStateStore.ForceChannelRefreshAsync(CancellationToken.None);
                _wakeSignal?.Pulse();
            }
        }

        public Task RemoveChannelAsync(Guid listId, string channelId)
        {
            return _listRepository.RemoveChannelAsync(listId, channelId);
        }

        public Task UpdateListAsync(Guid id, string title, decimal playbackRate)
        {
            return _listRepository.UpdateAsync(id, title, playbackRate);
        }

        public Task DeleteListAsync(Guid id)
        {
            return _listRepository.DeleteAsync(id);
        }

        private byte[] CreateToken()
        {
            var token = new byte[40];
            RandomNumberGenerator.Fill(token);
            return token;
        }

        private async Task<ListViewModel> GetListViewCoreAsync(ListModel list)
        {
            if (list == null)
            {
                return null;
            }

            var now = _clock.UtcNow;
            var projection = await _listRepository.GetVideoProjectionAsync(
                ToDomainList(list),
                Constants.ListRenderMaxItems + 1);
            if (projection == null)
            {
                return null;
            }

            return CreateListView(projection, now);
        }

        private static ListViewModel CreateListView(
            ListVideoProjection projection,
            DateTimeOffset now)
        {
            if (projection == null)
            {
                return null;
            }

            var videos = projection.Channels
                .SelectMany(channel => channel.Videos.Select(video => new VideoViewModel
                {
                    ChannelTitle = channel.Title,
                    ChannelUrl = channel.Url,
                    VideoId = video.VideoId,
                    VideoTitle = video.Title,
                    VideoDuration = video.Duration,
                    VideoPublishedAt = video.PublishedAt,
                    VideoThumbnail = video.ThumbnailUrl
                }))
                .OrderByDescending(video => video.VideoPublishedAt)
                .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                .ToList();

            return CreateViewModel(
                projection.List,
                MapChannels(projection.Channels),
                videos.Take(Constants.ListRenderMaxItems),
                videos.Count > Constants.ListRenderMaxItems,
                now);
        }

        private async Task<ListViewModel> GetListChannelViewCoreAsync(ListModel list)
        {
            if (list == null)
            {
                return null;
            }

            var now = _clock.UtcNow;
            var projection = await _listRepository.GetChannelProjectionAsync(ToDomainList(list));
            if (projection == null)
            {
                return null;
            }

            return CreateViewModel(
                projection.List,
                MapChannels(projection.Channels),
                Enumerable.Empty<VideoViewModel>(),
                false,
                now);
        }

        private static ListViewModel CreateViewModel(
            SubscriptionList list,
            IReadOnlyList<ChannelModel> channels,
            IEnumerable<VideoViewModel> videos,
            bool hasMoreVideos,
            DateTimeOffset now)
        {
            return new ListViewModel
            {
                Id = list.Id,
                Token = WebEncoders.Base64UrlEncode(list.Token ?? Array.Empty<byte>()),
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                Now = now,
                StaleCount = channels.Count(channel =>
                    channel.Status == ChannelStatus.Active &&
                    channel.StaleAfter <= now),
                HasMoreVideos = hasMoreVideos,
                Videos = videos.ToList(),
                Channels = channels,
                MaxAge = list.ExpiredAfter.Subtract(now)
            };
        }

        private static IReadOnlyList<ChannelModel> MapChannels(IEnumerable<ListVideoProjection.Channel> channels)
        {
            return channels.Select(channel => new ChannelModel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt
            }).ToList();
        }

        private static IReadOnlyList<ChannelModel> MapChannels(IEnumerable<ListChannelProjection.Channel> channels)
        {
            return channels.Select(channel => new ChannelModel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt
            }).ToList();
        }

        private DateTimeOffset CreateExpiredAfter(DateTimeOffset now)
        {
            return now.Add(_clock.RandomDelay(
                Constants.ListMaxAgeMin,
                Constants.ListMaxAgeMax));
        }

        private static ListModel ToListModel(SubscriptionList list)
        {
            if (list == null)
            {
                return null;
            }

            return new ListModel
            {
                Id = list.Id,
                Token = list.Token,
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                ExpirationRenewedOn = list.ExpirationRenewedOn
            };
        }

        private static SubscriptionList ToDomainList(ListModel list)
        {
            return new SubscriptionList
            {
                Id = list.Id,
                Token = list.Token,
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                ExpirationRenewedOn = list.ExpirationRenewedOn
            };
        }

        private static byte[] DecodeToken(string token)
        {
            if (token == null)
            {
                return null;
            }

            try
            {
                return WebEncoders.Base64UrlDecode(token);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
