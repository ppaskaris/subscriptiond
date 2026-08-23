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
        private readonly IChannelRepository _channelRepository;
        private readonly IAppClock _clock;
        private readonly IChannelRefreshQueue _refreshQueue;

        public ListService(
            IListRepository listRepository,
            IChannelRepository channelRepository,
            IAppClock clock,
            IChannelRefreshQueue refreshQueue)
        {
            _listRepository = listRepository;
            _channelRepository = channelRepository;
            _clock = clock;
            _refreshQueue = refreshQueue;
        }

        public async Task<SubscriptionList> CreateListAsync(string title)
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
            return list;
        }

        public Task<SubscriptionList> GetAuthenticatedListAsync(Guid id, string token)
        {
            return GetAuthenticatedDomainListAsync(id, token);
        }

        public async Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token)
        {
            var now = _clock.UtcNow;
            var list = await GetAuthenticatedDomainListAsync(id, token, now);
            return await CreateListViewAsync(list, includeVideos: true, now);
        }

        public Task<ListViewModel> GetListViewAsync(SubscriptionList list)
        {
            return GetListViewCoreAsync(list);
        }

        public Task<ListViewModel> GetListChannelViewAsync(SubscriptionList list)
        {
            return GetListChannelViewCoreAsync(list);
        }

        public async Task AddChannelAsync(Guid listId, string channelId)
        {
            await _listRepository.AddChannelAsync(listId, channelId);
            _refreshQueue.TryEnqueue(new ChannelRefreshRequest(
                channelId,
                ChannelRefreshReason.Missing));
        }

        public async Task ForceRefreshAsync(SubscriptionList list)
        {
            if (list == null)
            {
                return;
            }

            _refreshQueue.Enqueue(list.ChannelIds
                .Select(channelId => new ChannelRefreshRequest(
                    channelId,
                    ChannelRefreshReason.Forced))
                .ToList());
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

        private async Task<ListViewModel> GetListViewCoreAsync(SubscriptionList list)
        {
            if (list == null)
            {
                return null;
            }

            var now = _clock.UtcNow;
            return await CreateListViewAsync(list, includeVideos: true, now);
        }

        private async Task<ListViewModel> CreateListViewAsync(
            SubscriptionList list,
            bool includeVideos,
            DateTimeOffset now)
        {
            if (list == null)
            {
                return null;
            }

            var persistedChannels = await _channelRepository.GetBatchAsync(
                list.ChannelIds,
                CancellationToken.None);
            var channels = ComposeChannels(list.ChannelIds, persistedChannels);
            var videos = includeVideos
                ? MapVideos(persistedChannels)
                    .OrderByDescending(video => video.VideoPublishedAt)
                    .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                    .Take(Constants.ListRenderMaxItems + 1)
                    .ToList()
                : new List<VideoViewModel>();

            var view = CreateViewModel(
                list,
                channels,
                videos.Take(Constants.ListRenderMaxItems),
                videos.Count > Constants.ListRenderMaxItems,
                now);
            QueueRefreshCandidates(list.ChannelIds, channels, now);
            return view;
        }

        private static IReadOnlyList<ChannelModel> ComposeChannels(
            IReadOnlyList<string> channelIds,
            IReadOnlyList<Channel> persistedChannels)
        {
            var channelsById = persistedChannels.ToDictionary(
                channel => channel.Id,
                StringComparer.Ordinal);
            return channelIds
                .Select(channelId => channelsById.TryGetValue(channelId, out var channel)
                    ? MapChannel(channel)
                    : CreateMissingChannel(channelId))
                .OrderBy(channel => channel.Title, StringComparer.Ordinal)
                .ThenBy(channel => channel.Id, StringComparer.Ordinal)
                .ToArray();
        }

        private static ChannelModel MapChannel(Channel channel)
        {
            return new ChannelModel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt
            };
        }

        private static ChannelModel CreateMissingChannel(string channelId)
        {
            return new ChannelModel
            {
                Id = channelId,
                Url = string.Format(Constants.YoutubeChannelUrl, channelId),
                Title = "Temporarily unavailable",
                Status = ChannelStatus.Unavailable,
                StatusReason = ChannelStatusReason.None,
                IsMissing = true
            };
        }

        private async Task<SubscriptionList> GetAuthenticatedDomainListAsync(
            Guid id,
            string token,
            DateTimeOffset? nowOverride = null)
        {
            var decodedToken = DecodeToken(token);
            if (decodedToken == null)
            {
                return null;
            }

            var list = await _listRepository.GetAsync(id);
            if (list == null || TokenUtils.NotEqual(decodedToken, list.Token))
            {
                return null;
            }

            var now = nowOverride ?? _clock.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (list.ExpirationRenewedOn != today)
            {
                var expiredAfter = CreateExpiredAfter(now);
                list = await _listRepository.RenewExpirationAsync(list, expiredAfter, today);
            }

            return list;
        }

        private static IEnumerable<VideoViewModel> MapVideos(IEnumerable<Channel> channels)
        {
            return channels.SelectMany(channel => channel.Videos.Select(video => new VideoViewModel
                {
                    ChannelTitle = channel.Title,
                    ChannelUrl = channel.Url,
                    VideoId = video.VideoId,
                    VideoTitle = video.Title,
                    VideoDuration = video.Duration,
                    VideoPublishedAt = video.PublishedAt,
                    VideoThumbnail = video.ThumbnailUrl
                }));
        }

        private async Task<ListViewModel> GetListChannelViewCoreAsync(SubscriptionList list)
        {
            if (list == null)
            {
                return null;
            }

            var now = _clock.UtcNow;
            return await CreateListViewAsync(list, includeVideos: false, now);
        }

        private void QueueRefreshCandidates(
            IEnumerable<string> channelIds,
            IEnumerable<ChannelModel> channels,
            DateTimeOffset now)
        {
            if (channelIds == null || channels == null)
            {
                return;
            }

            var channelsById = channels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
            var candidates = new List<ChannelRefreshRequest>();
            foreach (var channelId in channelIds)
            {
                if (!channelsById.TryGetValue(channelId, out var channel) || channel.IsMissing)
                {
                    candidates.Add(new ChannelRefreshRequest(
                        channelId,
                        ChannelRefreshReason.Missing));
                }
                else if (channel.Status == ChannelStatus.Active && channel.StaleAfter <= now)
                {
                    candidates.Add(new ChannelRefreshRequest(
                        channelId,
                        ChannelRefreshReason.Stale,
                        channel.StaleAfter));
                }
            }

            _refreshQueue.Enqueue(candidates);
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

        private DateTimeOffset CreateExpiredAfter(DateTimeOffset now)
        {
            return now.Add(_clock.RandomDelay(
                Constants.ListMaxAgeMin,
                Constants.ListMaxAgeMax));
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
