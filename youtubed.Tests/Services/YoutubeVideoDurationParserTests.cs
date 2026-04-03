using System;
using System.Collections.Generic;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.Services
{
    public class YoutubeVideoDurationParserTests
    {
        [Theory]
        [InlineData("PT1M", 60)]
        [InlineData("PT5M7S", 307)]
        [InlineData("PT1H2M3S", 3723)]
        public void TryParse_ValidIsoDurations_ReturnsDuration(string value, int totalSeconds)
        {
            var success = YoutubeVideoDurationParser.TryParse(value, out var duration);

            Assert.True(success);
            Assert.Equal(TimeSpan.FromSeconds(totalSeconds), duration);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-duration")]
        public void TryParse_InvalidIsoDurations_ReturnsFalse(string value)
        {
            var success = YoutubeVideoDurationParser.TryParse(value, out var duration);

            Assert.False(success);
            Assert.Equal(default, duration);
        }

        [Fact]
        public void ParseById_FiltersShortsInvalidValuesAndMissingIds()
        {
            var result = YoutubeVideoDurationParser.ParseById(new[]
            {
                new KeyValuePair<string, string>("short", "PT59S"),
                new KeyValuePair<string, string>("three-minutes", "PT3M"),
                new KeyValuePair<string, string>("long-enough", "PT3M1S"),
                new KeyValuePair<string, string>("long", "PT1H2M3S"),
                new KeyValuePair<string, string>("invalid", "bogus"),
                new KeyValuePair<string, string>("", "PT5M"),
                new KeyValuePair<string, string>("missing", null)
            });

            Assert.Equal(2, result.Count);
            Assert.Equal(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)), result["long-enough"]);
            Assert.Equal(new TimeSpan(1, 2, 3), result["long"]);
            Assert.DoesNotContain("short", result.Keys);
            Assert.DoesNotContain("three-minutes", result.Keys);
            Assert.DoesNotContain("invalid", result.Keys);
        }

        [Fact]
        public void ParseById_LastDuplicateDurationWins()
        {
            var result = YoutubeVideoDurationParser.ParseById(new[]
            {
                new KeyValuePair<string, string>("video-1", "PT4M"),
                new KeyValuePair<string, string>("video-1", "PT5M")
            });

            Assert.Equal(TimeSpan.FromMinutes(5), result["video-1"]);
        }
    }
}
