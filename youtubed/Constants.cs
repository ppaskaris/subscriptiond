using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace youtubed
{
    public static class Constants
    {
        public const string YoutubeChannelPattern = @"^https:\/\/www\.youtube\.com\/(user|channel)\/([a-zA-Z0-9_-]+)$";
        public const string YoutubeVideoPattern = @"^https:\/\/www\.youtube\.com\/watch\?v=([a-zA-Z0-9_-]+)$";
        public const string YoutubeWatchUrl = @"https://www.youtube.com/embed/{0}?autoplay=1&rel=0";
        public const string YoutubeChannelUrl = @"https://www.youtube.com/channel/{0}";
        public static readonly Regex YoutubeChannelExpression = new Regex(YoutubeChannelPattern);
        public static readonly Regex YoutubeVideoExpression = new Regex(YoutubeVideoPattern);
        public static readonly TimeSpan ChannelMaxAgeMin = TimeSpan.FromMinutes(60);
        public static readonly TimeSpan ChannelMaxAgeMax = TimeSpan.FromMinutes(90);
        public static readonly TimeSpan ChannelUpdateFrequencyMin = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan ChannelUpdateFrequencyMax = TimeSpan.FromSeconds(35);
        public static readonly TimeSpan ListMaxAgeMin = TimeSpan.FromDays(45);
        public static readonly TimeSpan ListMaxAgeMax = TimeSpan.FromDays(47);
        public static readonly TimeSpan MaintenanceFrequencyMin = TimeSpan.FromMinutes(8);
        public static readonly TimeSpan MaintenanceFrequencyMax = TimeSpan.FromMinutes(12);
        public static readonly TimeSpan VideoMaxAge = TimeSpan.FromDays(30);
        public static readonly TimeSpan VisibilityTimeoutMin = TimeSpan.FromMinutes(4);
        public static readonly TimeSpan VisibilityTimeoutMax = TimeSpan.FromMinutes(6);
        public static readonly int ListRenderMaxItems = 100;

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
    }
}
