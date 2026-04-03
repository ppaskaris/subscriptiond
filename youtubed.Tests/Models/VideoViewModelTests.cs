using System;
using Xunit;
using youtubed.Models;

namespace youtubed.Tests.Models
{
    public class VideoViewModelTests
    {
        [Theory]
        [InlineData(65, "1:05")]
        [InlineData(754, "12:34")]
        public void FormattedVideoDuration_UnderOneHour_UsesMinuteSecondFormat(int totalSeconds, string expected)
        {
            var model = new VideoViewModel
            {
                VideoDuration = TimeSpan.FromSeconds(totalSeconds)
            };

            Assert.Equal(expected, model.FormattedVideoDuration);
        }

        [Theory]
        [InlineData(3600, "1:00:00")]
        [InlineData(3723, "1:02:03")]
        [InlineData(9915, "2:45:15")]
        public void FormattedVideoDuration_OneHourOrMore_UsesHourMinuteSecondFormat(int totalSeconds, string expected)
        {
            var model = new VideoViewModel
            {
                VideoDuration = TimeSpan.FromSeconds(totalSeconds)
            };

            Assert.Equal(expected, model.FormattedVideoDuration);
        }
    }
}
