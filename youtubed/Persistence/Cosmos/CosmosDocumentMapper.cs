using System;
using System.Collections.Generic;
using System.Linq;
using youtubed.Domain;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosDocumentMapper
    {
        internal const int MaximumChannelIds = 100;
        internal const int MaximumVideos = 100;

        public static CosmosListDocument ToDocument(
            SubscriptionList list,
            IEnumerable<string> channelIds,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(channelIds);

            var orderedChannelIds = channelIds
                .Select(id => id ?? throw new ArgumentException(
                    "Channel IDs cannot contain null values.", nameof(channelIds)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (orderedChannelIds.Length > MaximumChannelIds)
            {
                throw new ArgumentException(
                    $"A Cosmos list document cannot contain more than {MaximumChannelIds} channel IDs.",
                    nameof(channelIds));
            }

            return new CosmosListDocument
            {
                Id = list.Id.ToString("D"),
                Token = list.Token.ToArray(),
                Title = list.Title,
                PlaybackRate = list.PlaybackRate,
                ExpiredAfter = list.ExpiredAfter,
                ExpirationRenewedOn = list.ExpirationRenewedOn,
                ChannelIds = orderedChannelIds,
                Ttl = GetTtlSeconds(list.ExpiredAfter, now)
            };
        }

        public static SubscriptionList ToSubscriptionList(CosmosListDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return new SubscriptionList
            {
                Id = Guid.Parse(document.Id),
                Token = document.Token.ToArray(),
                Title = document.Title,
                PlaybackRate = document.PlaybackRate,
                ExpiredAfter = document.ExpiredAfter,
                ExpirationRenewedOn = document.ExpirationRenewedOn
            };
        }

        public static IReadOnlyList<string> ToChannelIds(CosmosListDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return document.ChannelIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        public static CosmosChannelDocument ToDocument(Channel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            return new CosmosChannelDocument
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                PlaylistId = channel.PlaylistId,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status.ToString(),
                StatusReason = channel.StatusReason == ChannelStatusReason.None
                    ? null
                    : channel.StatusReason.ToString(),
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = OrderVideos(channel.Videos)
                    .Select(ToVideoDocument)
                    .ToArray()
            };
        }

        public static Channel ToChannel(CosmosChannelDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
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
                Videos = document.Videos
                    .Select(video => ToVideo(video, document.Id))
                    .ToArray()
            };
        }

        public static CosmosShareLinkDocument ToDocument(ShareLink shareLink, DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(shareLink);
            return new CosmosShareLinkDocument
            {
                Id = shareLink.Password,
                ListId = shareLink.ListId.ToString("D"),
                CreatedAt = shareLink.CreatedAt,
                ExpiresAfter = shareLink.ExpiresAfter,
                UsedAt = shareLink.UsedAt,
                Ttl = GetTtlSeconds(
                    shareLink.ExpiresAfter + Constants.ShareLinkRetentionAfterExpiration,
                    now)
            };
        }

        public static ShareLink ToShareLink(CosmosShareLinkDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return new ShareLink
            {
                Password = document.Id,
                ListId = Guid.Parse(document.ListId),
                CreatedAt = document.CreatedAt,
                ExpiresAfter = document.ExpiresAfter,
                UsedAt = document.UsedAt
            };
        }

        public static int GetTtlSeconds(DateTimeOffset expiresAt, DateTimeOffset now)
        {
            return Math.Max(1, checked((int)Math.Ceiling((expiresAt - now).TotalSeconds)));
        }

        private static IEnumerable<ChannelVideo> OrderVideos(IEnumerable<ChannelVideo> videos)
        {
            return (videos ?? Array.Empty<ChannelVideo>())
                .OrderByDescending(video => video.PublishedAt)
                .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                .ThenBy(video => video.Title, StringComparer.Ordinal)
                .ThenBy(video => video.Duration)
                .ThenBy(video => video.ThumbnailUrl, StringComparer.Ordinal)
                .GroupBy(video => video.VideoId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderByDescending(video => video.PublishedAt)
                .ThenBy(video => video.VideoId, StringComparer.Ordinal)
                .Take(MaximumVideos);
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
                : throw new InvalidOperationException(
                    $"Unsupported channel status reason '{value}'.");
        }
    }
}
