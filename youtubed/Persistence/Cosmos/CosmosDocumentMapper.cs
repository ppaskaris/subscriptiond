using System;
using System.Linq;
using youtubed.Domain;

namespace youtubed.Persistence.Cosmos
{
    public static class CosmosDocumentMapper
    {
        public static CosmosListDocument ToDocument(SubscriptionList list, DateTimeOffset now)
        {
            return new CosmosListDocument
            {
                Id = list.Id.ToString("D"),
                Token = list.Token,
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                ExpirationRenewedOn = list.ExpirationRenewedOn,
                Ttl = GetTtlSeconds(list.ExpiredAfter, now)
            };
        }

        public static SubscriptionList ToSubscriptionList(CosmosListDocument document)
        {
            return new SubscriptionList
            {
                Id = Guid.Parse(document.Id),
                Token = document.Token,
                Title = document.Title,
                PlaybackRate = document.PlaybackRate,
                ExpiredAfter = document.ExpiredAfter,
                ExpirationRenewedOn = document.ExpirationRenewedOn
            };
        }

        public static CosmosProjectedChannelDocument ToProjectedChannelDocument(Channel channel)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status.ToString(),
                StatusReason = channel.StatusReason.ToString(),
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = channel.Videos.Select(ToVideoDocument).ToArray()
            };
        }

        public static CosmosChannelDocument ToChannelDocument(
            Channel channel,
            DateTimeOffset now,
            TimeSpan orphanRetention)
        {
            var isOrphaned = channel.SubscriptionCount == 0
                && channel.OrphanedAfter.HasValue;

            return new CosmosChannelDocument
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status.ToString(),
                StatusReason = channel.StatusReason.ToString(),
                StatusUpdatedAt = channel.StatusUpdatedAt,
                SubscribedListIds = channel.SubscribedListIds.Select(id => id.ToString("D")).ToArray(),
                SubscriptionCount = channel.SubscriptionCount,
                OrphanedAfter = channel.OrphanedAfter,
                Ttl = isOrphaned ? GetTtlSeconds(channel.OrphanedAfter.Value + orphanRetention, now) : -1,
                Videos = channel.Videos.Select(ToVideoDocument).ToArray()
            };
        }

        public static Channel ToChannel(CosmosProjectedChannelDocument document)
        {
            return new Channel
            {
                Id = document.Id,
                Url = document.Url,
                Title = document.Title,
                Thumbnail = document.Thumbnail,
                StaleAfter = document.StaleAfter,
                Status = ParseChannelStatus(document.Status),
                StatusReason = ParseChannelStatusReason(document.StatusReason),
                StatusUpdatedAt = document.StatusUpdatedAt,
                Videos = document.Videos.Select(video => ToVideo(video, document.Id)).ToArray()
            };
        }

        public static Channel ToChannel(CosmosChannelDocument document)
        {
            return new Channel
            {
                Id = document.Id,
                Url = document.Url,
                Title = document.Title,
                Thumbnail = document.Thumbnail,
                PlaylistId = document.PlaylistId,
                StaleAfter = document.StaleAfter,
                Status = ParseChannelStatus(document.Status),
                StatusReason = ParseChannelStatusReason(document.StatusReason),
                StatusUpdatedAt = document.StatusUpdatedAt,
                SubscribedListIds = document.SubscribedListIds.Select(Guid.Parse).ToArray(),
                SubscriptionCount = document.SubscriptionCount,
                OrphanedAfter = document.OrphanedAfter,
                Videos = document.Videos.Select(video => ToVideo(video, document.Id)).ToArray()
            };
        }

        public static CosmosShareLinkDocument ToDocument(ShareLink shareLink, DateTimeOffset now)
        {
            return new CosmosShareLinkDocument
            {
                Id = shareLink.Password,
                ListId = shareLink.ListId.ToString("D"),
                CreatedAt = shareLink.CreatedAt,
                ExpiresAfter = shareLink.ExpiresAfter,
                UsedAt = shareLink.UsedAt,
                Ttl = GetTtlSeconds(shareLink.ExpiresAfter + Constants.ShareLinkRetentionAfterExpiration, now)
            };
        }

        public static ShareLink ToShareLink(CosmosShareLinkDocument document)
        {
            return new ShareLink
            {
                Password = document.Id,
                ListId = Guid.Parse(document.ListId),
                CreatedAt = document.CreatedAt,
                ExpiresAfter = document.ExpiresAfter,
                UsedAt = document.UsedAt
            };
        }

        public static CosmosWorkerStateDocument ToDocument(WorkerState state)
        {
            return new CosmosWorkerStateDocument
            {
                NextChannelRefreshAt = state.NextChannelRefreshAt,
                ChannelRefreshForceCount = state.ChannelRefreshForceCount,
                NextPurgeAt = state.NextPurgeAt
            };
        }

        public static WorkerState ToWorkerState(CosmosWorkerStateDocument document)
        {
            return new WorkerState
            {
                NextChannelRefreshAt = document.NextChannelRefreshAt,
                ChannelRefreshForceCount = document.ChannelRefreshForceCount,
                NextPurgeAt = document.NextPurgeAt
            };
        }

        private static CosmosVideoDocument ToVideoDocument(ChannelVideo video)
        {
            return new CosmosVideoDocument
            {
                Id = video.VideoId,
                Title = video.Title,
                DurationTicks = video.Duration.Ticks,
                PublishedAt = video.PublishedAt,
                Thumbnail = video.ThumbnailUrl
            };
        }

        private static ChannelVideo ToVideo(CosmosVideoDocument document, string channelId)
        {
            return new ChannelVideo
            {
                VideoId = document.Id,
                ChannelId = channelId,
                Title = document.Title,
                Duration = TimeSpan.FromTicks(document.DurationTicks),
                PublishedAt = document.PublishedAt,
                ThumbnailUrl = document.Thumbnail
            };
        }

        private static int GetTtlSeconds(DateTimeOffset expiresAt, DateTimeOffset now)
        {
            return Math.Max(1, (int)Math.Ceiling((expiresAt - now).TotalSeconds));
        }

        private static ChannelStatus ParseChannelStatus(string value)
        {
            return Enum.TryParse(value, ignoreCase: true, out ChannelStatus status)
                ? status
                : throw new InvalidOperationException($"Unsupported channel status '{value}'.");
        }

        private static ChannelStatusReason ParseChannelStatusReason(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ChannelStatusReason.None;
            }

            return Enum.TryParse(value, ignoreCase: true, out ChannelStatusReason reason)
                ? reason
                : throw new InvalidOperationException($"Unsupported channel status reason '{value}'.");
        }
    }
}
