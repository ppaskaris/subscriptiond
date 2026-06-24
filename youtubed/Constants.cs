using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace youtubed
{
    public static class Constants
    {
        public const string YoutubeChannelPattern = @"^https:\/\/www\.youtube\.com\/(user|channel)\/([a-zA-Z0-9_-]+)$";
        public const string YoutubeVideoPattern = @"^https:\/\/www\.youtube\.com\/watch\?v=([a-zA-Z0-9_-]+)$";
        public const string YoutubeWatchUrl = @"https://www.youtube.com/watch?v={0}";
        public const string YoutubeEmbedUrl = @"https://www.youtube-nocookie.com/embed/{0}";
        public const string YoutubeChannelUrl = @"https://www.youtube.com/channel/{0}";
        public static readonly Regex YoutubeChannelExpression = new Regex(YoutubeChannelPattern);
        public static readonly Regex YoutubeVideoExpression = new Regex(YoutubeVideoPattern);
        public static readonly TimeSpan ChannelMaxAgeMin = TimeSpan.FromMinutes(60);
        public static readonly TimeSpan ChannelMaxAgeMax = TimeSpan.FromMinutes(90);
        public static readonly TimeSpan ChannelLookupCacheDuration = TimeSpan.FromHours(24);
        public const int ChannelLookupCacheSizeLimit = 1000;
        public static readonly TimeSpan ChannelUpdateFrequencyMin = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan ChannelUpdateFrequencyMax = TimeSpan.FromSeconds(35);
        public static readonly TimeSpan ChannelUnavailableStaleDelay = TimeSpan.FromDays(36500);
        public const int ChannelRefreshBatchSize = 10;
        public const int ChannelRefreshLookaheadMultiplier = 10;
        public const int ChannelRefreshLookaheadCount = ChannelRefreshBatchSize * ChannelRefreshLookaheadMultiplier;
        public static readonly TimeSpan YoutubeCallDelay = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ListMaxAgeMin = TimeSpan.FromDays(45);
        public static readonly TimeSpan ListMaxAgeMax = TimeSpan.FromDays(47);
        public static readonly TimeSpan MaintenanceFrequencyMin = TimeSpan.FromMinutes(8);
        public static readonly TimeSpan MaintenanceFrequencyMax = TimeSpan.FromMinutes(12);
        public static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan ShareLinkMaxAgeMin = TimeSpan.FromMinutes(65);
        public static readonly TimeSpan ShareLinkMaxAgeMax = TimeSpan.FromMinutes(75);
        public static readonly TimeSpan ShareLinkRetentionAfterExpiration = TimeSpan.FromDays(1);
        public static readonly TimeSpan ShortsMaxDuration = TimeSpan.FromMinutes(3);
        public static readonly TimeSpan VideoMaxAge = TimeSpan.FromDays(30);
        public static readonly TimeSpan VisibilityTimeoutMin = TimeSpan.FromMinutes(4);
        public static readonly TimeSpan VisibilityTimeoutMax = TimeSpan.FromMinutes(6);
        public static readonly int ListRenderMaxItems = 100;
        public static readonly decimal DefaultListPlaybackRate = 1.00m;
        public static readonly decimal DefaultWatchPlaybackRate = 1.00m;
        public static readonly decimal[] YoutubePlaybackRates =
        {
            0.25m,
            0.50m,
            0.75m,
            1.00m,
            1.25m,
            1.50m,
            1.75m,
            2.00m
        };

        private static readonly Random _random = new Random();

        public static TimeSpan RandomlyBetween(TimeSpan min, TimeSpan max)
        {
            if (max < min)
            {
                throw new ArgumentException("Value is too small.", nameof(max));
            }
            ulong value;
            ulong range = (ulong)(max.Ticks - min.Ticks);
            ulong threshold = ulong.MaxValue - (ulong.MaxValue % range);
            byte[] buffer = new byte[8];
            do
            {
                _random.NextBytes(buffer);
                value = (ulong)BitConverter.ToInt64(buffer, 0);
            } while (value >= threshold);
            return TimeSpan.FromTicks((long)(value % range) + min.Ticks);
        }

        public static bool IsSupportedPlaybackRate(decimal playbackRate)
        {
            return YoutubePlaybackRates.Contains(playbackRate);
        }

        public static string FormatPlaybackRate(decimal playbackRate)
        {
            return playbackRate.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
