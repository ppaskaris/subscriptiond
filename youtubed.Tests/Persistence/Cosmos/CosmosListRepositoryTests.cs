using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosListRepositoryTests
    {
        [Fact]
        public async Task UpdateAsync_RecomputesTtlFromAbsoluteExpirationBeforeReplacing()
        {
            var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var document = CreateListDocument(listId, "etag-1");
            document.ExpiredAfter = now.AddHours(1);
            document.Ttl = (int)TimeSpan.FromDays(45).TotalSeconds;
            var lists = new Mock<Container>();
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            var repository = new CosmosListRepository(
                lists.Object,
                new Mock<Container>().Object,
                new FakeAppClock { UtcNow = now });

            await repository.UpdateAsync(listId, "Updated", 1.5m);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(value => value.Ttl == 3600),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddChannelAsync_RereadsAndRetriesOnceAfterEtagConflict()
        {
            const string channelId = "channel-1";
            var listId = Guid.NewGuid();
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            var channelResponse = CreateResponse(new CosmosChannelDocument
            {
                Id = channelId,
                Title = "Channel",
                Url = "https://www.youtube.com/channel/channel-1",
                Thumbnail = "channel.png",
                PlaylistId = "playlist-1",
                StaleAfter = DateTimeOffset.UtcNow,
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString()
            });
            var firstRead = CreateResponse(CreateListDocument(listId, "etag-1"));
            var retryRead = CreateResponse(CreateListDocument(listId, "etag-2"));

            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channelResponse.Object);
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstRead.Object)
                .ReturnsAsync(retryRead.Object)
                .ReturnsAsync(retryRead.Object);
            lists
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);

            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock());

            await repository.AddChannelAsync(listId, channelId);

            lists.Verify(container => container.ReadItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Exactly(3));
            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.ETag == "etag-2"
                    && document.Channels.Count == 1
                    && document.Channels[0].Id == channelId),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AddChannelAsync_SeedsProjectionWithSharedSizingPolicy()
        {
            const string channelId = "channel-10";
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            var listDocument = CreateListDocument(listId, "etag-1");
            listDocument.Channels = Enumerable.Range(1, 9)
                .Select(index => new CosmosProjectedChannelDocument
                {
                    Id = $"channel-{index:D2}"
                })
                .ToArray();
            var channelDocument = new CosmosChannelDocument
            {
                Id = channelId,
                Title = "Channel",
                Url = "https://www.youtube.com/channel/channel-10",
                Thumbnail = "channel.png",
                PlaylistId = "playlist-10",
                StaleAfter = now,
                Status = ChannelStatus.Active.ToString(),
                StatusReason = ChannelStatusReason.None.ToString(),
                Videos = Enumerable.Range(0, 20)
                    .Select(index => new CosmosVideoDocument
                    {
                        Id = $"video-{index:D2}",
                        PublishedAt = now.AddDays(-10).AddMinutes(-index)
                    })
                    .ToArray()
            };
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    channelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(channelDocument).Object);
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(listDocument).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.AddChannelAsync(listId, channelId);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.Channels.Single(channel => channel.Id == channelId).Videos.Count == 14),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelAsync_RehydratesUnderfilledActiveAndUnavailableChannels()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string removedChannelId = "channel-removed";
            const string activeChannelId = "channel-active";
            const string unavailableChannelId = "channel-unavailable";
            var listDocument = CreateListDocument(listId, "list-etag");
            listDocument.Channels = new[]
            {
                CreateProjectedChannel(activeChannelId, ChannelStatus.Active, now, 45),
                CreateProjectedChannel(unavailableChannelId, ChannelStatus.Unavailable, now, 45),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 45)
            };
            var activeDocument = CreateCanonicalChannelDocument(
                activeChannelId,
                ChannelStatus.Active,
                now,
                100);
            var unavailableDocument = CreateCanonicalChannelDocument(
                unavailableChannelId,
                ChannelStatus.Unavailable,
                now,
                100);
            var removedDocument = CreateCanonicalChannelDocument(
                removedChannelId,
                ChannelStatus.Active,
                now,
                100);
            removedDocument.SubscribedListIds = new[] { listId.ToString("D") };
            removedDocument.SubscriptionCount = 1;
            removedDocument.ETag = "removed-etag";
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(listDocument).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    activeChannelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(activeDocument).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    unavailableChannelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(unavailableDocument).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    removedChannelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(removedDocument).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    removedChannelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.Channels.Count == 2
                    && document.Channels.Single(channel =>
                        channel.Id == activeChannelId).Videos.Count == 67
                    && document.Channels.Single(channel =>
                        channel.Id == unavailableChannelId).Videos.Count == 67
                    && document.Channels.Single(channel =>
                        channel.Id == unavailableChannelId).Status
                        == ChannelStatus.Unavailable.ToString()),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "list-etag"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelAsync_DoesNotReadFullyAllocatedRemainingChannel()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string remainingChannelId = "channel-remaining";
            const string removedChannelId = "channel-removed";
            var listDocument = CreateListDocument(listId, "list-etag");
            listDocument.Channels = new[]
            {
                CreateProjectedChannel(remainingChannelId, ChannelStatus.Active, now, 100),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 67)
            };
            var removedDocument = CreateCanonicalChannelDocument(
                removedChannelId,
                ChannelStatus.Active,
                now,
                100);
            removedDocument.SubscribedListIds = new[] { listId.ToString("D") };
            removedDocument.SubscriptionCount = 1;
            removedDocument.ETag = "removed-etag";
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(listDocument).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    removedChannelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(removedDocument).Object);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    removedChannelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            channels.Verify(container => container.ReadItemAsync<CosmosChannelDocument>(
                remainingChannelId,
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task RemoveChannelAsync_FallsBackToEmbeddedProjectionsAtVideoCapacity()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string removedChannelId = "channel-removed";
            var remainingChannelIds = Enumerable.Range(0, 6)
                .Select(index => $"channel-{index}")
                .ToArray();
            var listDocument = CreateListDocument(listId, "list-etag");
            listDocument.Channels = remainingChannelIds
                .Select(id => CreateProjectedChannel(
                    id,
                    ChannelStatus.Active,
                    now,
                    20))
                .Append(CreateProjectedChannel(
                    removedChannelId,
                    ChannelStatus.Active,
                    now,
                    20))
                .ToArray();
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            SetupListReplace(lists, listId, listDocument);
            foreach (var remainingChannelId in remainingChannelIds)
            {
                var canonical = CreateCanonicalChannelDocument(
                    remainingChannelId,
                    ChannelStatus.Active,
                    now,
                    100);
                canonical.Videos = canonical.Videos
                    .Select((video, index) =>
                    {
                        video.PublishedAt = now.AddMinutes(-index);
                        return video;
                    })
                    .ToArray();
                SetupChannelRead(channels, canonical);
            }

            SetupRemovedChannel(channels, listId, removedChannelId, now);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.Channels.Count == 6
                    && document.Channels.Sum(channel => channel.Videos.Count) == 120),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelAsync_FallsBackToEmbeddedProjectionAtByteCeiling()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string remainingChannelId = "channel-remaining";
            const string removedChannelId = "channel-removed";
            var listDocument = CreateListDocument(listId, "list-etag");
            listDocument.Channels = new[]
            {
                CreateProjectedChannel(remainingChannelId, ChannelStatus.Active, now, 20),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 20)
            };
            var oversizedCanonical = CreateCanonicalChannelDocument(
                remainingChannelId,
                ChannelStatus.Active,
                now,
                100);
            oversizedCanonical.Videos = oversizedCanonical.Videos
                .Select(video =>
                {
                    video.Thumbnail = new string('t', 19_000);
                    return video;
                })
                .ToArray();
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            SetupListReplace(lists, listId, listDocument);
            SetupChannelRead(channels, oversizedCanonical);
            SetupRemovedChannel(channels, listId, removedChannelId, now);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.Channels.Count == 1
                    && document.Channels[0].Id == remainingChannelId
                    && document.Channels[0].Videos.Count == 20),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelAsync_RetryRecomputesHydrationAfterMembershipChanged()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string removedChannelId = "channel-removed";
            const string channelA = "channel-a";
            const string channelB = "channel-b";
            const string channelC = "channel-c";
            var firstDocument = CreateListDocument(listId, "etag-1");
            firstDocument.Channels = new[]
            {
                CreateProjectedChannel(channelA, ChannelStatus.Active, now, 45),
                CreateProjectedChannel(channelB, ChannelStatus.Active, now, 45),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 45)
            };
            var retryDocument = CreateListDocument(listId, "etag-2");
            retryDocument.Channels = new[]
            {
                CreateProjectedChannel(channelA, ChannelStatus.Active, now, 100),
                CreateProjectedChannel(channelC, ChannelStatus.Active, now, 45),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 45)
            };
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(firstDocument).Object)
                .ReturnsAsync(CreateResponse(retryDocument).Object)
                .ReturnsAsync(CreateResponse(retryDocument).Object);
            lists
                .SetupSequence(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            foreach (var channelId in new[] { channelA, channelB, channelC })
            {
                SetupChannelRead(
                    channels,
                    CreateCanonicalChannelDocument(
                        channelId,
                        ChannelStatus.Active,
                        now,
                        100));
            }

            SetupRemovedChannel(channels, listId, removedChannelId, now);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            VerifyChannelRead(channels, channelA, Times.Once());
            VerifyChannelRead(channels, channelB, Times.Once());
            VerifyChannelRead(channels, channelC, Times.Once());
            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.ETag == "etag-2"
                    && document.Channels.Count == 2
                    && document.Channels.Single(channel =>
                        channel.Id == channelA).Videos.Count == 67
                    && document.Channels.Single(channel =>
                        channel.Id == channelC).Videos.Count == 67),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RemoveChannelAsync_CanonicalChannelNotFoundUsesEmbeddedProjection()
        {
            var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            var listId = Guid.NewGuid();
            const string remainingChannelId = "channel-missing";
            const string removedChannelId = "channel-removed";
            var listDocument = CreateListDocument(listId, "list-etag");
            listDocument.Channels = new[]
            {
                CreateProjectedChannel(remainingChannelId, ChannelStatus.Active, now, 14),
                CreateProjectedChannel(removedChannelId, ChannelStatus.Active, now, 14)
            };
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            SetupListReplace(lists, listId, listDocument);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    remainingChannelId,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "missing",
                    HttpStatusCode.NotFound,
                    0,
                    null,
                    0));
            SetupRemovedChannel(channels, listId, removedChannelId, now);
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock { UtcNow = now });

            await repository.RemoveChannelAsync(listId, removedChannelId);

            lists.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosListDocument>(document =>
                    document.Channels.Count == 1
                    && document.Channels[0].Id == remainingChannelId
                    && document.Channels[0].Videos.Count == 14),
                listId.ToString("D"),
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
            channels.Verify(container => container.ReplaceItemAsync(
                It.Is<CosmosChannelDocument>(document =>
                    document.Id == removedChannelId
                    && document.SubscriptionCount == 0),
                removedChannelId,
                It.IsAny<PartitionKey?>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteAsync_RereadsAfterConflictAndReconcilesExactDeletedVersion()
        {
            var listId = Guid.NewGuid();
            var firstDocument = CreateListDocument(listId, "etag-1");
            firstDocument.Channels = new[]
            {
                new CosmosProjectedChannelDocument { Id = "channel-1" }
            };
            var retryDocument = CreateListDocument(listId, "etag-2");
            retryDocument.Channels = new[]
            {
                new CosmosProjectedChannelDocument { Id = "channel-1" },
                new CosmosProjectedChannelDocument { Id = "channel-2" }
            };
            var lists = new Mock<Container>();
            var channels = new Mock<Container>();
            lists
                .SetupSequence(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(firstDocument).Object)
                .ReturnsAsync(CreateResponse(retryDocument).Object);
            lists
                .SetupSequence(container => container.DeleteItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException(
                    "conflict",
                    HttpStatusCode.PreconditionFailed,
                    0,
                    null,
                    0))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new CosmosException("missing", HttpStatusCode.NotFound, 0, null, 0));
            var repository = new CosmosListRepository(
                lists.Object,
                channels.Object,
                new FakeAppClock());

            await repository.DeleteAsync(listId);

            lists.Verify(container => container.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-1"),
                It.IsAny<CancellationToken>()), Times.Once);
            lists.Verify(container => container.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                It.IsAny<PartitionKey>(),
                It.Is<ItemRequestOptions>(options => options.IfMatchEtag == "etag-2"),
                It.IsAny<CancellationToken>()), Times.Once);
            channels.Verify(container => container.ReadItemAsync<CosmosChannelDocument>(
                "channel-2",
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private static CosmosListDocument CreateListDocument(Guid id, string etag)
        {
            return new CosmosListDocument
            {
                Id = id.ToString("D"),
                Token = new byte[] { 1 },
                Title = "List",
                ExpiredAfter = DateTimeOffset.UtcNow.AddDays(1),
                ETag = etag
            };
        }

        private static CosmosProjectedChannelDocument CreateProjectedChannel(
            string id,
            ChannelStatus status,
            DateTimeOffset now,
            int videoCount)
        {
            return new CosmosProjectedChannelDocument
            {
                Id = id,
                Status = status.ToString(),
                StatusReason = status == ChannelStatus.Active
                    ? ChannelStatusReason.None.ToString()
                    : ChannelStatusReason.NotFound.ToString(),
                Videos = Enumerable.Range(0, videoCount)
                    .Select(index => new CosmosVideoDocument
                    {
                        Id = $"{id}-video-{index:D3}",
                        PublishedAt = now.AddDays(-10).AddMinutes(-index)
                    })
                    .ToArray()
            };
        }

        private static CosmosChannelDocument CreateCanonicalChannelDocument(
            string id,
            ChannelStatus status,
            DateTimeOffset now,
            int videoCount)
        {
            return new CosmosChannelDocument
            {
                Id = id,
                Status = status.ToString(),
                StatusReason = status == ChannelStatus.Active
                    ? ChannelStatusReason.None.ToString()
                    : ChannelStatusReason.NotFound.ToString(),
                StaleAfter = now,
                Videos = Enumerable.Range(0, videoCount)
                    .Select(index => new CosmosVideoDocument
                    {
                        Id = $"{id}-video-{index:D3}",
                        PublishedAt = now.AddDays(-10).AddMinutes(-index)
                    })
                    .ToArray()
            };
        }

        private static void SetupListReplace(
            Mock<Container> lists,
            Guid listId,
            CosmosListDocument document)
        {
            lists
                .Setup(container => container.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
            lists
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosListDocument>(),
                    listId.ToString("D"),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosListDocument>(null).Object);
        }

        private static void SetupChannelRead(
            Mock<Container> channels,
            CosmosChannelDocument document)
        {
            channels
                .Setup(container => container.ReadItemAsync<CosmosChannelDocument>(
                    document.Id,
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse(document).Object);
        }

        private static void SetupRemovedChannel(
            Mock<Container> channels,
            Guid listId,
            string channelId,
            DateTimeOffset now)
        {
            var removed = CreateCanonicalChannelDocument(
                channelId,
                ChannelStatus.Active,
                now,
                0);
            removed.SubscribedListIds = new[] { listId.ToString("D") };
            removed.SubscriptionCount = 1;
            removed.ETag = "removed-etag";
            SetupChannelRead(channels, removed);
            channels
                .Setup(container => container.ReplaceItemAsync(
                    It.IsAny<CosmosChannelDocument>(),
                    channelId,
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateResponse<CosmosChannelDocument>(null).Object);
        }

        private static void VerifyChannelRead(
            Mock<Container> channels,
            string channelId,
            Times times)
        {
            channels.Verify(container => container.ReadItemAsync<CosmosChannelDocument>(
                channelId,
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()), times);
        }

        private static Mock<ItemResponse<T>> CreateResponse<T>(T resource)
        {
            var response = new Mock<ItemResponse<T>>();
            response.SetupGet(value => value.Resource).Returns(resource);
            return response;
        }
    }
}
