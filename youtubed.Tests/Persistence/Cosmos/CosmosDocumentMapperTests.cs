using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosDocumentMapperTests
    {
        private static readonly DateTimeOffset Now =
            new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public void ListMappingRoundTripsAndOrdersMembershipDeterministically()
        {
            var list = CreateList();
            list.ChannelIds = new[] { "UC-z", "UC-a", "UC-z", "UC-m" };

            var document = CosmosDocumentMapper.ToDocument(list, Now);

            Assert.Equal(new[] { "UC-a", "UC-m", "UC-z" }, document.ChannelIds);
            Assert.Equal(3974400, document.Ttl);
            var roundTrip = CosmosDocumentMapper.ToSubscriptionList(document);
            Assert.Equal(list.Id, roundTrip.Id);
            Assert.Equal(list.Token, roundTrip.Token);
            Assert.Equal(list.Title, roundTrip.Title);
            Assert.Equal(list.PlaybackRate, roundTrip.PlaybackRate);
            Assert.Equal(list.ExpiredAfter, roundTrip.ExpiredAfter);
            Assert.Equal(list.ExpirationRenewedOn, roundTrip.ExpirationRenewedOn);
            Assert.Equal(new[] { "UC-a", "UC-m", "UC-z" }, roundTrip.ChannelIds);
        }

        [Fact]
        public void ListMappingDefaultsMissingOrNullMembershipToEmpty()
        {
            const string json = """
                {
                  "id": "5fd6b227-3961-4bf9-9a27-b4cfc9b47b28",
                  "token": "AQIDBA==",
                  "title": "Subscriptions",
                  "playbackRate": 1.25,
                  "expiredAfter": "2026-09-29T12:00:00+00:00",
                  "expirationRenewedOn": "2026-08-14",
                  "ttl": 3974400
                }
                """;
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var document = CosmosSystemTextJsonSerializer.Instance
                .FromStream<CosmosListDocument>(stream);

            Assert.Empty(CosmosDocumentMapper.ToSubscriptionList(document).ChannelIds);

            document.ChannelIds = null;
            Assert.Empty(CosmosDocumentMapper.ToSubscriptionList(document).ChannelIds);
        }

        [Fact]
        public void ListMappingRejectsNullOrBlankChannelIds()
        {
            var list = CreateList();
            list.ChannelIds = new[] { "UC-valid", null };
            Assert.Throws<ArgumentException>(() => CosmosDocumentMapper.ToDocument(list, Now));

            list.ChannelIds = new[] { "UC-valid", " " };
            Assert.Throws<ArgumentException>(() => CosmosDocumentMapper.ToDocument(list, Now));
        }

        [Fact]
        public void ListMappingSupportsOneHundredChannelsAndRejectsTheHundredAndFirst()
        {
            var list = CreateList();
            list.ChannelIds = Enumerable.Range(0, 100)
                .Select(value => $"UC-{value:D3}")
                .Reverse()
                .ToArray();

            var document = CosmosDocumentMapper.ToDocument(list, Now);

            Assert.Equal(100, document.ChannelIds.Count);
            Assert.Equal("UC-000", document.ChannelIds[0]);
            Assert.Equal("UC-099", document.ChannelIds[99]);

            list.ChannelIds = Enumerable.Range(0, 101)
                .Select(value => $"UC-{value:D3}")
                .ToArray();

            var exception = Assert.Throws<ArgumentException>(() =>
                CosmosDocumentMapper.ToDocument(list, Now));

            Assert.Contains("100", exception.Message);
        }

        [Fact]
        public void ChannelMappingDeduplicatesBoundsAndOrdersVideosDeterministically()
        {
            var videos = Enumerable.Range(0, 102)
                .Select(value => CreateVideo(
                    $"video-{value:D3}",
                    Now.AddMinutes(-(value % 3))))
                .Append(CreateVideo("video-001", Now.AddMinutes(1), "newer duplicate"))
                .Reverse()
                .ToArray();
            var channel = CreateChannel(videos);

            var document = CosmosDocumentMapper.ToDocument(channel);

            Assert.Equal(100, document.Videos.Count);
            Assert.Equal("video-001", document.Videos[0].Id);
            Assert.Equal("newer duplicate", document.Videos[0].Title);
            Assert.Equal(
                document.Videos
                    .OrderByDescending(video => video.PublishedAt)
                    .ThenBy(video => video.Id, StringComparer.Ordinal)
                    .Select(video => video.Id),
                document.Videos.Select(video => video.Id));
            Assert.Equal(100, document.Videos.Select(video => video.Id).Distinct().Count());

            var roundTrip = CosmosDocumentMapper.ToChannel(document);
            Assert.Equal(channel.Id, roundTrip.Id);
            Assert.Equal(ChannelStatus.Active, roundTrip.Status);
            Assert.Equal(ChannelStatusReason.None, roundTrip.StatusReason);
            Assert.All(roundTrip.Videos, video => Assert.Equal(channel.Id, video.ChannelId));
        }

        [Fact]
        public void ShareLinkMappingAddsDiagnosticRetentionToTtlAndStoresNoListToken()
        {
            var link = new ShareLink
            {
                Password = "four-word-password",
                ListId = Guid.Parse("5fd6b227-3961-4bf9-9a27-b4cfc9b47b28"),
                CreatedAt = Now,
                ExpiresAfter = Now.AddMinutes(70)
            };

            var document = CosmosDocumentMapper.ToDocument(link, Now);
            var json = CosmosSystemTextJsonSerializer.Instance.SerializeToString(document);

            Assert.Equal(90600, document.Ttl);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(link.Password, CosmosDocumentMapper.ToShareLink(document).Password);
            Assert.Equal(link.ListId, CosmosDocumentMapper.ToShareLink(document).ListId);
        }

        [Fact]
        public void ExpiredDocumentsReceiveMinimumPositiveTtl()
        {
            Assert.Equal(1, CosmosDocumentMapper.GetTtlSeconds(Now.AddDays(-1), Now));
            Assert.Equal(1, CosmosDocumentMapper.GetTtlSeconds(Now, Now));
            Assert.Equal(2, CosmosDocumentMapper.GetTtlSeconds(Now.AddSeconds(1.1), Now));
        }

        [Fact]
        public void SharedSerializerProducesExpectedDocumentNamesAndRepresentativeSizes()
        {
            var subscriptionList = CreateList();
            subscriptionList.ChannelIds = Enumerable.Range(0, 100)
                .Select(value => $"UC{new string('x', 20)}{value:D3}")
                .ToArray();
            var list = CosmosDocumentMapper.ToDocument(subscriptionList, Now);
            var videos = Enumerable.Range(0, 100)
                .Select(value => CreateVideo(
                    $"video-{value:D3}",
                    Now.AddMinutes(-value),
                    new string('t', 200)))
                .ToArray();
            var channel = CosmosDocumentMapper.ToDocument(CreateChannel(videos));

            var listJson = CosmosSystemTextJsonSerializer.Instance.SerializeToString(list);
            using var parsed = JsonDocument.Parse(listJson);
            Assert.True(parsed.RootElement.TryGetProperty("channelIds", out _));
            Assert.True(parsed.RootElement.TryGetProperty("expirationRenewedOn", out _));
            Assert.False(parsed.RootElement.TryGetProperty("_etag", out _));

            var listSize = CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(list);
            var channelSize = CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(channel);
            Assert.InRange(listSize, 1, 32 * 1024);
            Assert.InRange(channelSize, 1, 256 * 1024);
        }

        private static SubscriptionList CreateList()
        {
            return new SubscriptionList
            {
                Id = Guid.Parse("5fd6b227-3961-4bf9-9a27-b4cfc9b47b28"),
                Token = new byte[] { 1, 2, 3, 4 },
                Title = "Subscriptions",
                PlaybackRate = 1.25m,
                ExpiredAfter = Now.AddDays(46),
                ExpirationRenewedOn = DateOnly.FromDateTime(Now.UtcDateTime)
            };
        }

        private static Channel CreateChannel(ChannelVideo[] videos)
        {
            return new Channel
            {
                Id = "UC-channel",
                Url = "https://www.youtube.com/channel/UC-channel",
                Title = "Channel title",
                Thumbnail = "https://example.test/channel.jpg",
                PlaylistId = "UU-channel",
                StaleAfter = Now.AddHours(1),
                Status = ChannelStatus.Active,
                StatusReason = ChannelStatusReason.None,
                Videos = videos
            };
        }

        private static ChannelVideo CreateVideo(
            string id,
            DateTimeOffset publishedAt,
            string title = null)
        {
            return new ChannelVideo
            {
                VideoId = id,
                ChannelId = "UC-channel",
                Title = title ?? id,
                Duration = TimeSpan.FromMinutes(3),
                PublishedAt = publishedAt,
                ThumbnailUrl = $"https://example.test/{id}.jpg"
            };
        }
    }
}
