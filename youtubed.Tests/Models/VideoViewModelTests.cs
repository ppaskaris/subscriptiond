using System;
using Xunit;
using youtubed.Models;

namespace youtubed.Tests.Models
{
    public class VideoViewModelTests
    {
        [Fact]
        public void WatchUrl_UsesInternalWatchRoute()
        {
            var model = new VideoViewModel
            {
                VideoId = "video-1"
            };

            Assert.Equal("/watch/video-1", model.WatchUrl);
        }

        [Fact]
        public void WatchUrl_WithVideoTitle_AppendsEncodedTitleQuery()
        {
            var model = new VideoViewModel
            {
                VideoId = "video-1",
                VideoTitle = "Test &amp; Video"
            };

            Assert.Equal("/watch/video-1?title=Test+%26amp%3B+Video", model.WatchUrl);
        }

        [Fact]
        public void GetWatchUrl_WithPlaybackRate_AppendsPlaybackRateQuery()
        {
            var model = new VideoViewModel
            {
                VideoId = "video-1"
            };

            Assert.Equal("/watch/video-1?playbackRate=1.5", model.GetWatchUrl(1.50m));
        }

        [Fact]
        public void GetWatchUrl_WithVideoTitleAndPlaybackRate_AppendsEncodedQuery()
        {
            var model = new VideoViewModel
            {
                VideoId = "video-1",
                VideoTitle = "Test &amp; Video"
            };

            Assert.Equal("/watch/video-1?title=Test+%26amp%3B+Video&playbackRate=2", model.GetWatchUrl(2.00m));
        }

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
