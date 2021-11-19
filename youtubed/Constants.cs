using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace youtubed
{
    public static class Constants
    {
        public const string YoutubePattern = @"^https:\/\/www\.youtube\.com\/(user|channel)\/([a-zA-Z0-9_-]+)$";
        public const string YoutubeWatchUrl = @"https://www.youtube.com/embed/{0}?autoplay=1&rel=0";
        public static readonly Regex YoutubeExpression = new Regex(YoutubePattern);
        public static readonly TimeSpan ChannelMaxAgeMax = TimeSpan.FromHours(3.5);
        public static readonly TimeSpan ChannelMaxAgeMin = TimeSpan.FromHours(2.5);
        public static readonly TimeSpan ChannelUpdateFrequencyMax = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan ChannelUpdateFrequencyMin = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ListMaxAgeMax = TimeSpan.FromDays(47);
        public static readonly TimeSpan ListMaxAgeMin = TimeSpan.FromDays(45);
        public static readonly TimeSpan MaintenanceFrequencyMax = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MaintenanceFrequencyMin = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan VideoMaxAge = TimeSpan.FromDays(30);
        public static readonly TimeSpan VisibilityTimeoutMax = TimeSpan.FromMinutes(5.5);
        public static readonly TimeSpan VisibilityTimeoutMin = TimeSpan.FromMinutes(4.5);

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
