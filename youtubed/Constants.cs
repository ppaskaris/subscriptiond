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
        public const string YoutubeVideoUrl = @"https://www.youtube.com/watch_popup?v={0}";
        public static readonly Regex YoutubeExpression = new Regex(YoutubePattern);
        public static readonly TimeSpan UpdateFrequency = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan UpdateMaxAge = TimeSpan.FromHours(6);
        public static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30);
    }
}
