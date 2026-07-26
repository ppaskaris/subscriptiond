using System;
using System.Collections.Generic;
using System.Linq;

namespace youtubed.Persistence.Cosmos
{
    internal static class CosmosListProjectionPolicy
    {
        internal const int MaxChannelsPerList = 100;
        internal const int MaxCanonicalVideosPerChannel = 100;
        internal const int MaxProjectedVideosPerList = 500;
        internal const int SerializedSizeSafetyCeilingBytes = 1_900_000;
        internal const double PointReadRuBudget = 350;
        internal const double ProjectionWriteRuBudget = 3_000;
        internal const int PerChannelMinimum = Constants.ListProjectionPerChannelMin;
        internal const decimal OversamplingFactor = Constants.ListProjectionOversamplingFactor;
        internal static readonly TimeSpan RecentVideoAge = Constants.ListProjectionRecentVideoAge;

        internal static CosmosListDocument CreateBoundedCopy(
            CosmosListDocument document,
            DateTimeOffset now)
        {
            ArgumentNullException.ThrowIfNull(document);

            var sourceChannels =
                document.Channels ?? Array.Empty<CosmosProjectedChannelDocument>();
            if (sourceChannels.Count > MaxChannelsPerList)
            {
                throw new ListCapacityExceededException(
                    $"A list can contain at most {MaxChannelsPerList} channels.");
            }

            var targetPerChannel = GetTargetVideoCountPerChannel(sourceChannels.Count);
            var recentCutoff = now.Subtract(RecentVideoAge);
            var channels = sourceChannels
                .OrderBy(channel => channel.Id, StringComparer.Ordinal)
                .Select(channel => CreateBoundedChannel(
                    channel,
                    targetPerChannel,
                    recentCutoff))
                .ToArray();
            var projectedVideoCount = channels.Sum(channel => channel.Videos.Count);
            if (projectedVideoCount > MaxProjectedVideosPerList)
            {
                throw new ListCapacityExceededException(
                    $"A list projection can contain at most {MaxProjectedVideosPerList} videos.");
            }

            var boundedDocument = new CosmosListDocument
            {
                Id = document.Id,
                Token = document.Token,
                Title = document.Title,
                PlaybackRate = document.PlaybackRate,
                ExpiredAfter = document.ExpiredAfter,
                ExpirationRenewedOn = document.ExpirationRenewedOn,
                Ttl = document.Ttl,
                MembershipVersion = document.MembershipVersion,
                MembershipRecoveryPending = document.MembershipRecoveryPending,
                MembershipRecoveryDueAt = document.MembershipRecoveryDueAt,
                MembershipRecoveryStartedAt = document.MembershipRecoveryStartedAt,
                MembershipRecoveryAttempt = document.MembershipRecoveryAttempt,
                MembershipRecoveryPoison = document.MembershipRecoveryPoison,
                MembershipRecoveryLastErrorClass = document.MembershipRecoveryLastErrorClass,
                Channels = channels,
                ETag = document.ETag
            };
            var serializedSize = GetSerializedSizeBytes(boundedDocument);
            if (serializedSize >= SerializedSizeSafetyCeilingBytes)
            {
                throw new ListCapacityExceededException(
                    $"The list projection exceeds the {SerializedSizeSafetyCeilingBytes}-byte safety ceiling.");
            }

            return boundedDocument;
        }

        internal static int GetTargetVideoCountPerChannel(int channelCount)
        {
            if (channelCount <= 0)
            {
                return 0;
            }

            var oversampledShare = (int)Math.Ceiling(
                Constants.ListRenderMaxItems / (decimal)channelCount * OversamplingFactor);
            return Math.Min(
                MaxCanonicalVideosPerChannel,
                Math.Max(PerChannelMinimum, oversampledShare));
        }

        internal static int GetSerializedSizeBytes(CosmosListDocument document)
        {
            using var stream = CosmosSystemTextJsonSerializer.Instance.ToStream(document);
            return checked((int)stream.Length);
        }

        private static CosmosProjectedChannelDocument CreateBoundedChannel(
            CosmosProjectedChannelDocument channel,
            int targetVideoCount,
            DateTimeOffset recentCutoff)
        {
            var videos = (channel.Videos ?? Array.Empty<CosmosVideoDocument>())
                .Where(video => video != null)
                .OrderByDescending(video => video.PublishedAt)
                .ThenBy(video => video.Id, StringComparer.Ordinal)
                .DistinctBy(video => video.Id, StringComparer.Ordinal)
                .Take(MaxCanonicalVideosPerChannel)
                .ToArray();
            var recentVideoCount = videos.Count(video => video.PublishedAt >= recentCutoff);

            return new CosmosProjectedChannelDocument
            {
                Id = channel.Id,
                Url = channel.Url,
                Title = channel.Title,
                Thumbnail = channel.Thumbnail,
                StaleAfter = channel.StaleAfter,
                Status = channel.Status,
                StatusReason = channel.StatusReason,
                StatusUpdatedAt = channel.StatusUpdatedAt,
                Videos = videos
                    .Take(Math.Max(recentVideoCount, targetVideoCount))
                    .Select(CreateVideoCopy)
                    .ToArray()
            };
        }

        private static CosmosVideoDocument CreateVideoCopy(CosmosVideoDocument video)
        {
            return new CosmosVideoDocument
            {
                Id = video.Id,
                Title = video.Title,
                DurationTicks = video.DurationTicks,
                PublishedAt = video.PublishedAt,
                Thumbnail = video.Thumbnail
            };
        }
    }
}
