using System;
using System.Linq;
using Xunit;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosListProjectionPolicyTests
    {
        private static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void Apply_RetainsEveryVideoInRecentWindow()
        {
            var document = CreateDocument(
                Enumerable.Range(0, 20)
                    .Select(index => CreateChannel(
                        $"channel-{index:D2}",
                        index == 0
                            ? Enumerable.Range(0, 10)
                                .Select(video => CreateVideo(
                                    $"recent-{video:D2}",
                                    Now.Subtract(CosmosListProjectionPolicy.RecentVideoAge)
                                        .AddMinutes(video)))
                                .Concat(Enumerable.Range(0, 10).Select(video =>
                                    CreateVideo($"old-{video:D2}", Now.AddDays(-10).AddMinutes(video))))
                                .ToArray()
                            : Array.Empty<CosmosVideoDocument>()))
                    .ToArray());

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Equal(
                Enumerable.Range(0, 10).Select(index => $"recent-{index:D2}"),
                document.Channels.Single(channel => channel.Id == "channel-00")
                    .Videos.Select(video => video.Id)
                    .OrderBy(id => id, StringComparer.Ordinal));
        }

        [Fact]
        public void Apply_UsesOversampledPerChannelAllocationForOlderVideos()
        {
            var document = CreateDocument(
                Enumerable.Range(0, 10)
                    .Select(index => CreateChannel(
                        $"channel-{index:D2}",
                        Enumerable.Range(0, 20)
                            .Select(video => CreateVideo(
                                $"video-{video:D2}",
                                Now.AddDays(-10).AddMinutes(-video)))
                            .ToArray()))
                    .ToArray());

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Equal(14, CosmosListProjectionPolicy.GetTargetVideoCountPerChannel(10));
            Assert.All(document.Channels, channel => Assert.Equal(14, channel.Videos.Count));
        }

        [Fact]
        public void Apply_OrdersChannelAndVideoTiesDeterministicallyAndRemovesDuplicateVideos()
        {
            var document = CreateDocument(
                CreateChannel(
                    "channel-b",
                    CreateVideo("video-b", Now.AddDays(-10)),
                    CreateVideo("video-a", Now.AddDays(-10)),
                    CreateVideo("video-a", Now.AddDays(-11))),
                CreateChannel("channel-a"));

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Equal(new[] { "channel-a", "channel-b" }, document.Channels.Select(channel => channel.Id));
            Assert.Equal(
                new[] { "video-a", "video-b" },
                document.Channels[1].Videos.Select(video => video.Id));
        }

        [Fact]
        public void Apply_LeavesEmptyListEmpty()
        {
            var document = CreateDocument();

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Empty(document.Channels);
            Assert.Equal(0, CosmosListProjectionPolicy.GetTargetVideoCountPerChannel(0));
        }

        [Fact]
        public void Apply_OneChannelRetainsCanonicalMaximum()
        {
            var document = CreateDocument(
                CreateChannel(
                    "only-channel",
                    Enumerable.Range(0, 120)
                        .Select(index => CreateVideo(
                            $"video-{index:D3}",
                            Now.AddDays(-10).AddMinutes(-index)))
                        .ToArray()));

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Equal(
                CosmosListProjectionPolicy.MaxCanonicalVideosPerChannel,
                Assert.Single(document.Channels).Videos.Count);
        }

        [Fact]
        public void Apply_HighChannelCountUsesMinimumAndStaysWithinVideoLimit()
        {
            var document = CreateDocument(
                Enumerable.Range(0, CosmosListProjectionPolicy.MaxChannelsPerList)
                    .Reverse()
                    .Select(index => CreateChannel(
                        $"channel-{index:D3}",
                        Enumerable.Range(0, 10)
                            .Select(video => CreateVideo(
                                $"video-{video:D2}",
                                Now.AddDays(-10).AddMinutes(-video)))
                            .ToArray()))
                    .ToArray());

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.Equal(
                CosmosListProjectionPolicy.PerChannelMinimum,
                CosmosListProjectionPolicy.GetTargetVideoCountPerChannel(
                    CosmosListProjectionPolicy.MaxChannelsPerList));
            Assert.Equal(
                CosmosListProjectionPolicy.MaxProjectedVideosPerList,
                document.Channels.Sum(channel => channel.Videos.Count));
            Assert.Equal("channel-000", document.Channels[0].Id);
            Assert.Equal("channel-099", document.Channels[^1].Id);
        }

        [Fact]
        public void RepresentativeMaximumCardinalityDocumentStaysBelowUtf8SafetyCeiling()
        {
            var document = CreateDocument(
                Enumerable.Range(0, CosmosListProjectionPolicy.MaxChannelsPerList)
                    .Select(channel => new CosmosProjectedChannelDocument
                    {
                        Id = new string((char)('a' + channel % 26), 50) + channel.ToString("D3"),
                        Url = "https://www.youtube.com/channel/" + new string('c', 215),
                        Title = new string('界', 250),
                        Thumbnail = "https://i.ytimg.com/" + new string('t', 1980),
                        StaleAfter = Now,
                        Status = "Active",
                        StatusReason = "None",
                        Videos = Enumerable.Range(0, CosmosListProjectionPolicy.PerChannelMinimum)
                            .Select(video => new CosmosVideoDocument
                            {
                                Id = new string('v', 47) + video.ToString("D3"),
                                Title = new string('界', 100),
                                DurationTicks = TimeSpan.FromHours(1).Ticks,
                                PublishedAt = Now.AddDays(-10).AddMinutes(-video),
                                Thumbnail = "https://i.ytimg.com/" + new string('t', 1980)
                            })
                            .ToArray()
                    })
                    .ToArray());

            document = CosmosListProjectionPolicy.CreateBoundedCopy(document, Now);

            Assert.True(
                CosmosListProjectionPolicy.GetSerializedSizeBytes(document)
                    < CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes);
        }

        [Fact]
        public void Apply_RejectsUnsupportedRecentVideoVolumeBeforeWrite()
        {
            var document = CreateDocument(
                Enumerable.Range(0, 6)
                    .Select(channel => CreateChannel(
                        $"channel-{channel}",
                        Enumerable.Range(0, 100)
                            .Select(video => CreateVideo(
                                $"video-{video:D3}",
                                Now.AddMinutes(-video)))
                            .ToArray()))
                    .ToArray());

            var exception = Assert.Throws<ListCapacityExceededException>(
                () => CosmosListProjectionPolicy.CreateBoundedCopy(document, Now));

            Assert.Contains(
                CosmosListProjectionPolicy.MaxProjectedVideosPerList.ToString(),
                exception.Message);
        }

        [Fact]
        public void Apply_RejectsSerializedDocumentAtSafetyCeilingBeforeWrite()
        {
            var document = CreateDocument(
                CreateChannel("channel", CreateVideo("video", Now)));
            document.Channels[0].Videos[0].Thumbnail =
                new string('x', CosmosListProjectionPolicy.SerializedSizeSafetyCeilingBytes);

            var exception = Assert.Throws<ListCapacityExceededException>(
                () => CosmosListProjectionPolicy.CreateBoundedCopy(document, Now));

            Assert.Contains("safety ceiling", exception.Message);
        }

        private static CosmosListDocument CreateDocument(
            params CosmosProjectedChannelDocument[] channels)
        {
            return new CosmosListDocument
            {
                Id = Guid.NewGuid().ToString("D"),
                Token = new byte[40],
                Title = "Projection policy test",
                PlaybackRate = 1,
                ExpiredAfter = Now.AddDays(45),
                Ttl = (int)TimeSpan.FromDays(45).TotalSeconds,
                Channels = channels
            };
        }

        private static CosmosProjectedChannelDocument CreateChannel(
            string id,
            params CosmosVideoDocument[] videos)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = id,
                Thumbnail = $"{id}.png",
                StaleAfter = Now,
                Status = "Active",
                StatusReason = "None",
                Videos = videos
            };
        }

        private static CosmosVideoDocument CreateVideo(string id, DateTimeOffset publishedAt)
        {
            return new CosmosVideoDocument
            {
                Id = id,
                Title = id,
                DurationTicks = TimeSpan.FromMinutes(5).Ticks,
                PublishedAt = publishedAt,
                Thumbnail = $"{id}.png"
            };
        }
    }
}
