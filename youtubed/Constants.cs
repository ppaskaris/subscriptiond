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
        public const string YoutubeWatchUrl = @"https://www.youtube.com/embed/{0}?autoplay=1";
        public static readonly Regex YoutubeExpression = new Regex(YoutubePattern);
        public static readonly TimeSpan UpdateFrequencyMin = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan UpdateFrequencyMax = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan UpdateMaxAgeMin = TimeSpan.FromMinutes(60);
        public static readonly TimeSpan UpdateMaxAgeMax = TimeSpan.FromMinutes(90);
        public static readonly TimeSpan VisibilityTimeoutMin = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan VisibilityTimeoutMax = TimeSpan.FromSeconds(60);

        private static readonly Random _random = new Random();

        public static TimeSpan RandomlyBetween(TimeSpan lower, TimeSpan upper)
        {
            if (upper < lower)
            {
                throw new ArgumentException("Value is too small.", nameof(upper));
            }
            ulong value;
            ulong range = (ulong)(upper.Ticks - lower.Ticks);
            byte[] buffer = new byte[8];
            do
            {
                _random.NextBytes(buffer);
                value = (ulong)BitConverter.ToInt64(buffer, 0);
            } while (value >= ulong.MaxValue - (ulong.MaxValue % range));
            return TimeSpan.FromTicks((long)(value % range) + lower.Ticks);
        }
    }
}
