using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class ProviderContractTestBase : IAsyncLifetime
    {
        protected static readonly DateTimeOffset DefaultNow =
            new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        private readonly IProviderContractTestFixture _fixture;

        protected ProviderContractTestBase(IProviderContractTestFixture fixture)
        {
            _fixture = fixture;
            Clock = new FakeAppClock();
        }

        protected FakeAppClock Clock { get; }

        protected ProviderContractTestContext Provider { get; private set; }

        protected string ProviderName => _fixture.ProviderName;

        public async Task InitializeAsync()
        {
            Clock.UtcNow = DefaultNow;
            Clock.RandomDelayValue = null;

            await _fixture.ResetAsync();
            Provider = _fixture.CreateContext(Clock);
        }

        public Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

        protected async Task<SubscriptionList> CreateListAsync(
            Guid? id = null,
            string title = "Contract List",
            decimal? playbackRate = null,
            DateTimeOffset? expiredAfter = null,
            DateOnly? expirationRenewedOn = null,
            byte[] token = null,
            IReadOnlyList<string> channelIds = null)
        {
            var list = new SubscriptionList
            {
                Id = id ?? Guid.NewGuid(),
                Token = token ?? CreateToken(1),
                Title = title,
                PlaybackRate = playbackRate ?? Constants.DefaultListPlaybackRate,
                ExpiredAfter = expiredAfter ?? Clock.UtcNow.AddDays(45),
                ExpirationRenewedOn = expirationRenewedOn,
                ChannelIds = channelIds ?? Array.Empty<string>()
            };

            await Provider.Lists.CreateAsync(list);
            return list;
        }

        protected async Task<Channel> CreateChannelAsync(
            string id = null,
            string title = "Contract Channel",
            string playlistId = null,
            DateTimeOffset? staleAfter = null)
        {
            id ??= CreateUniqueId("contract-channel");
            playlistId ??= CreateUniqueId("playlist");
            var channelStaleAfter = staleAfter ?? Clock.UtcNow.AddMinutes(-5);
            var channel = new Channel
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = title,
                Thumbnail = $"{id}.png",
                PlaylistId = playlistId,
                StaleAfter = channelStaleAfter
            };

            await Provider.Channels.SaveDiscoveredChannelAsync(
                channel,
                channelStaleAfter);

            return channel;
        }

        protected Task AddChannelToListAsync(Guid listId, string channelId)
        {
            return Provider.Lists.AddChannelAsync(listId, channelId);
        }

        protected async Task<ShareLink> CreateShareLinkAsync(
            Guid listId,
            string password = null)
        {
            password ??= CreateUniqueId("contract-share-link");
            var shareLink = new ShareLink
            {
                Password = password,
                ListId = listId,
                CreatedAt = Clock.UtcNow,
                ExpiresAfter = Clock.UtcNow.AddHours(1)
            };

            var created = await Provider.ShareLinks.TryCreateAsync(shareLink);
            Assert.True(created);
            return shareLink;
        }

        protected async Task SaveVideosAsync(
            Channel channel,
            params ChannelVideo[] videos)
        {
            var result = new ChannelRefreshResult
            {
                Channel = ToDomainChannel(channel, videos),
                VideosRefreshed = true,
                EarliestPublishedAt = videos.Length == 0
                    ? Clock.UtcNow
                    : videos.Min(video => video.PublishedAt)
            };

            await Provider.Channels.SaveRefreshResultAsync(result, CancellationToken.None);
        }

        protected ChannelVideo CreateVideo(
            string channelId,
            string videoId = null,
            string title = "Contract Video",
            DateTimeOffset? publishedAt = null)
        {
            videoId ??= CreateUniqueId("contract-video");
            return new ChannelVideo
            {
                ChannelId = channelId,
                VideoId = videoId,
                Title = title,
                Duration = TimeSpan.FromMinutes(3),
                PublishedAt = publishedAt ?? Clock.UtcNow.AddMinutes(-30),
                ThumbnailUrl = $"{videoId}.png"
            };
        }

        private static byte[] CreateToken(byte value)
        {
            return Enumerable.Repeat(value, 40).ToArray();
        }

        private static string CreateUniqueId(string prefix)
        {
            return $"{prefix}-{Guid.NewGuid():N}";
        }

        protected static Channel ToDomainChannel(
            Channel channel,
            IReadOnlyCollection<ChannelVideo> videos)
        {
            return new Channel
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = videos.ToArray()
            };
        }
    }
}
