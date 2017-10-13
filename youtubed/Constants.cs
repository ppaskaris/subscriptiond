using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace youtubed
{
    public static class Constants
    {
        public const string YoutubePattern = @"^https:\/\/www\.youtube\.com\/(user|channel)\/([a-zA-Z0-9_]+)$";
        public static readonly Regex YoutubeExpression = new Regex(YoutubePattern);
        public static readonly TimeSpan UpdateFrequency = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan UpdateMaxAge = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30);
    }
}
