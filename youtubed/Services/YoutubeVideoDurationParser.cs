using System;
using System.Collections.Generic;
using System.Xml;

namespace youtubed.Services
{
    internal static class YoutubeVideoDurationParser
    {
        public static bool TryParse(string value, out TimeSpan duration)
        {
            try
            {
                duration = XmlConvert.ToTimeSpan(value);
                return true;
            }
            catch (ArgumentNullException)
            {
                duration = default;
                return false;
            }
            catch (FormatException)
            {
                duration = default;
                return false;
            }
        }

        public static IReadOnlyDictionary<string, TimeSpan> ParseById(IEnumerable<KeyValuePair<string, string>> durationsById)
        {
            var results = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

            foreach (var pair in durationsById)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                if (!TryParse(pair.Value, out var duration))
                {
                    continue;
                }

                if (duration <= TimeSpan.FromMinutes(3))
                {
                    continue;
                }

                results[pair.Key] = duration;
            }

            return results;
        }
    }
}
