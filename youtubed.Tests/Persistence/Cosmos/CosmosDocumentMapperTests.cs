using System;
using System.Linq;
using System.Text.Json;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosDocumentMapperTests
    {
        [Fact]
        public void ListDocumentRoundTripsAndSerializesTtl()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var list = new SubscriptionList
            {
                Id = Guid.Parse("5fd6b227-3961-4bf9-9a27-b4cfc9b47b28"),
                Token = new byte[] { 1, 2, 3 },
                Title = "Subscriptions",
                PlaybackRate = 1.25m,
                ExpiredAfter = now.AddDays(45),
                ExpirationRenewedOn = DateOnly.FromDateTime(now.UtcDateTime)
            };

            var document = CosmosDocumentMapper.ToDocument(list, now);
            var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            Assert.Contains("\"ttl\":3888000", json);
            Assert.Equal(list.Id, CosmosDocumentMapper.ToSubscriptionList(document).Id);
            Assert.Equal(list.Token, CosmosDocumentMapper.ToSubscriptionList(document).Token);
        }

        [Fact]
        public void ChannelTtlIsEnabledOnlyForOrphans()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var channel = new Channel
            {
                Id = "UC123",
                SubscriptionCount = 0,
                OrphanedAfter = now
            };

            var orphan = CosmosDocumentMapper.ToChannelDocument(channel, now, TimeSpan.FromDays(7));
            channel.SubscriptionCount = 1;
            var subscribed = CosmosDocumentMapper.ToChannelDocument(channel, now, TimeSpan.FromDays(7));

            Assert.Equal(604800, orphan.Ttl);
            Assert.Equal(-1, subscribed.Ttl);
        }

        [Fact]
        public void ShareLinkTtlIncludesRetentionAfterExpiration()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var link = new ShareLink
            {
                Password = "four-word-password",
                ListId = Guid.NewGuid(),
                CreatedAt = now,
                ExpiresAfter = now.AddHours(1)
            };

            var document = CosmosDocumentMapper.ToDocument(link, now);

            Assert.Equal((int)TimeSpan.FromDays(1).Add(TimeSpan.FromHours(1)).TotalSeconds, document.Ttl);
            Assert.Equal(link, CosmosDocumentMapper.ToShareLink(document), new ShareLinkComparer());
        }

        [Fact]
        public void NullProjectedChannelStatusReasonMapsToNone()
        {
            var channel = CosmosDocumentMapper.ToChannel(new CosmosProjectedChannelDocument
            {
                Id = "UC123",
                Status = "Active",
                StatusReason = null
            });

            Assert.Equal(ChannelStatusReason.None, channel.StatusReason);
        }

        [Fact]
        public void ListProjectionsReshapeEmbeddedChannelsAndApplyGlobalVideoLimit()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var document = CosmosDocumentMapper.ToDocument(new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Title = "Projected List",
                ExpiredAfter = now.AddDays(1)
            }, now);
            document.Channels = new[]
            {
                CreateProjectedChannel("channel-b", "Beta", now, ("oldest", -3), ("newest", -1)),
                CreateProjectedChannel("channel-a", "Alpha", now, ("middle", -2))
            };

            var channelProjection = CosmosDocumentMapper.ToChannelProjection(document);
            var videoProjection = CosmosDocumentMapper.ToVideoProjection(document, 2);

            Assert.Equal(
                new[] { "channel-a", "channel-b" },
                channelProjection.Channels.Select(channel => channel.Id));
            Assert.Equal(
                new[] { "newest", "middle" },
                videoProjection.Channels
                    .SelectMany(channel => channel.Videos)
                    .OrderByDescending(video => video.PublishedAt)
                    .Select(video => video.VideoId));
        }

        private static CosmosProjectedChannelDocument CreateProjectedChannel(
            string id,
            string title,
            DateTimeOffset now,
            params (string Id, int PublishedHoursAgo)[] videos)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = id,
                Title = title,
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString(),
                Videos = videos.Select(video => new CosmosVideoDocument
                {
                    Id = video.Id,
                    PublishedAt = now.AddHours(video.PublishedHoursAgo)
                }).ToArray()
            };
        }

        private sealed class ShareLinkComparer : System.Collections.Generic.IEqualityComparer<ShareLink>
        {
            public bool Equals(ShareLink x, ShareLink y)
            {
                return x.Password == y.Password
                    && x.ListId == y.ListId
                    && x.CreatedAt == y.CreatedAt
                    && x.ExpiresAfter == y.ExpiresAfter
                    && x.UsedAt == y.UsedAt;
            }

            public int GetHashCode(ShareLink obj) => obj.Password.GetHashCode();
        }
    }
}
