using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosRepositoryTests
    {
        private static readonly DateTimeOffset Now =
            new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task LoadedAggregateRenewalUsesItsEtagWithoutAnotherPointRead()
        {
            var client = new FakeCosmosRepositoryClient();
            var clock = CreateClock();
            var repository = new CosmosListRepository(client, clock);
            var list = CreateList(expirationRenewedOn: clock.UtcToday.AddDays(-1));
            await repository.CreateAsync(list);

            var loaded = await repository.GetAsync(list.Id);
            loaded = await repository.RenewExpirationAsync(
                loaded,
                Now.AddDays(46),
                clock.UtcToday);

            Assert.Equal(Now.AddDays(46), client.GetList(list.Id).ExpiredAfter);
            Assert.Equal(clock.UtcToday, client.GetList(list.Id).ExpirationRenewedOn);
            Assert.Equal(1, client.ListReplaceCount);
            Assert.Equal(1, client.ListReadCount);

            await repository.RenewExpirationAsync(
                loaded,
                Now.AddDays(47),
                clock.UtcToday);
            Assert.Equal(1, client.ListReplaceCount);
            Assert.Equal(1, client.ListReadCount);
        }

        [Fact]
        public async Task MembershipIsSortedDistinctAndRejectsTheHundredAndFirstChannel()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);

            foreach (var index in Enumerable.Range(0, 100).Reverse())
            {
                await repository.AddChannelAsync(list.Id, $"UC-{index:D3}");
            }
            await repository.AddChannelAsync(list.Id, "UC-050");

            var document = client.GetList(list.Id);
            Assert.Equal(100, document.ChannelIds.Count);
            Assert.Equal(
                document.ChannelIds.OrderBy(id => id, StringComparer.Ordinal),
                document.ChannelIds);
            await Assert.ThrowsAsync<ListCapacityExceededException>(() =>
                repository.AddChannelAsync(list.Id, "UC-100"));
        }

        [Fact]
        public async Task CreateGetAndSettingsUpdatePreserveNormalizedMembership()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            list.ChannelIds = new[] { "UC-z", "UC-a", "UC-z" };

            await repository.CreateAsync(list);

            var created = await repository.GetAsync(list.Id);
            Assert.Equal(new[] { "UC-a", "UC-z" }, created.ChannelIds);

            await repository.UpdateAsync(list.Id, "Updated", 1.5m);

            var updated = await repository.GetAsync(list.Id);
            Assert.Equal("Updated", updated.Title);
            Assert.Equal(1.5m, updated.PlaybackRate);
            Assert.Equal(new[] { "UC-a", "UC-z" }, updated.ChannelIds);
        }

        [Fact]
        public async Task ListMutationRetriesOneConflictAndLetsASecondConflictEscape()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);
            client.ListReplaceConflictsRemaining = 1;

            await repository.AddChannelAsync(list.Id, "UC-retried");

            Assert.Equal(new[] { "UC-retried" }, client.GetList(list.Id).ChannelIds);
            Assert.Equal(2, client.ListReplaceAttemptCount);
            Assert.Equal(2, client.ListReadCount);

            client.ListReplaceConflictsRemaining = 2;
            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.AddChannelAsync(list.Id, "UC-fails"));
            Assert.DoesNotContain("UC-fails", client.GetList(list.Id).ChannelIds);
        }

        [Fact]
        public async Task RenewalRetriesOneConflictAndLetsASecondConflictEscape()
        {
            var client = new FakeCosmosRepositoryClient();
            var clock = CreateClock();
            var repository = new CosmosListRepository(client, clock);
            var list = CreateList(expirationRenewedOn: clock.UtcToday.AddDays(-1));
            await repository.CreateAsync(list);
            var loaded = await repository.GetAsync(list.Id);
            client.ListReplaceConflictsRemaining = 2;

            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.RenewExpirationAsync(
                    loaded,
                    Now.AddDays(46),
                    clock.UtcToday));

            Assert.Equal(2, client.ListReplaceAttemptCount);
            Assert.Equal(2, client.ListReadCount);
            Assert.NotEqual(clock.UtcToday, client.GetList(list.Id).ExpirationRenewedOn);
        }

        [Fact]
        public async Task ListReadsReturnOnlyMembershipAndNeverReadChannelDocuments()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosListRepository(client, CreateClock());
            var list = CreateList();
            await repository.CreateAsync(list);
            await repository.AddChannelAsync(list.Id, "UC-present");
            await repository.AddChannelAsync(list.Id, "UC-missing");
            var aggregate = await repository.GetAsync(list.Id);

            Assert.Equal(new[] { "UC-missing", "UC-present" }, aggregate.ChannelIds);
            Assert.Equal(0, client.ChannelReadManyCount);
        }

        [Fact]
        public async Task ListDeleteKeepsNotFoundIdempotencyAtRepositoryCallSite()
        {
            var client = new FakeCosmosRepositoryClient
            {
                DeleteListException = CosmosFailure(HttpStatusCode.NotFound)
            };
            var repository = new CosmosListRepository(client, CreateClock());

            await repository.DeleteAsync(Guid.NewGuid());

            var conflict = CosmosFailure(HttpStatusCode.Conflict);
            client.DeleteListException = conflict;
            var thrown = await Assert.ThrowsAsync<CosmosException>(() =>
                repository.DeleteAsync(Guid.NewGuid()));
            Assert.Same(conflict, thrown);
        }

        [Fact]
        public async Task ChannelRefreshBoundsVideosRetriesOnceAndPreservesVideosWhenNotRefreshed()
        {
            var client = new FakeCosmosRepositoryClient();
            var repository = new CosmosChannelRepository(client);
            var channel = CreateChannel();
            await repository.SaveDiscoveredChannelAsync(channel, channel.StaleAfter);
            var refreshed = CreateChannel();
            refreshed.Title = "Refreshed";
            refreshed.Videos = Enumerable.Range(0, 105)
                .Select(index => new ChannelVideo
                {
                    ChannelId = channel.Id,
                    VideoId = $"video-{index:D3}",
                    Title = $"Video {index}",
                    Duration = TimeSpan.FromMinutes(1),
                    PublishedAt = Now.AddMinutes(-index),
                    ThumbnailUrl = $"{index}.jpg"
                })
                .ToArray();
            client.ChannelReplaceConflictsRemaining = 1;

            await repository.SaveRefreshResultAsync(
                new ChannelRefreshResult { Channel = refreshed, VideosRefreshed = true },
                CancellationToken.None);

            var saved = await repository.GetByIdAsync(channel.Id);
            Assert.Equal("Refreshed", saved.Title);
            Assert.Equal(100, saved.Videos.Count);
            Assert.Equal(2, client.ChannelReplaceAttemptCount);

            var metadataOnly = CreateChannel();
            metadataOnly.Title = "Metadata only";
            metadataOnly.Videos = Array.Empty<ChannelVideo>();
            await repository.SaveRefreshResultAsync(
                new ChannelRefreshResult { Channel = metadataOnly, VideosRefreshed = false },
                CancellationToken.None);
            Assert.Equal(100, (await repository.GetByIdAsync(channel.Id)).Videos.Count);

            client.ChannelReplaceConflictsRemaining = 2;
            await Assert.ThrowsAsync<CosmosException>(() =>
                repository.SaveRefreshResultAsync(
                    new ChannelRefreshResult { Channel = refreshed },
                    CancellationToken.None));
        }

        private static FakeAppClock CreateClock()
        {
            return new FakeAppClock { UtcNow = Now };
        }

        private static SubscriptionList CreateList(DateOnly? expirationRenewedOn = null)
        {
            return new SubscriptionList
            {
                Id = Guid.NewGuid(),
                Token = new byte[] { 1, 2, 3 },
                Title = "List",
                PlaybackRate = 1.25m,
                ExpiredAfter = Now.AddDays(45),
                ExpirationRenewedOn = expirationRenewedOn
            };
        }

        private static Channel CreateChannel()
        {
            return new Channel
            {
                Id = "UC-channel",
                Url = "https://www.youtube.com/channel/UC-channel",
                Title = "Channel",
                Thumbnail = "channel.jpg",
                PlaylistId = "UU-channel",
                StaleAfter = Now.AddHours(1)
            };
        }

        private static CosmosException CosmosFailure(HttpStatusCode status)
        {
            return new CosmosException(
                "Cosmos operation failed.",
                status,
                subStatusCode: 0,
                activityId: string.Empty,
                requestCharge: 0);
        }

        private static CosmosChannelDocument CreateChannelDocument(
            string id,
            IReadOnlyList<CosmosVideoDocument> videos)
        {
            return new CosmosChannelDocument
            {
                Id = id,
                Url = $"https://www.youtube.com/channel/{id}",
                Title = id,
                Thumbnail = $"{id}.jpg",
                PlaylistId = id.Replace("UC", "UU", StringComparison.Ordinal),
                StaleAfter = Now.AddHours(1),
                Status = ChannelStatus.Active.ToString(),
                Videos = videos
            };
        }

        private sealed class FakeCosmosRepositoryClient : ICosmosRepositoryClient
        {
            private readonly Dictionary<string, CosmosListDocument> _lists = new(StringComparer.Ordinal);
            private readonly Dictionary<string, CosmosChannelDocument> _channels = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

            public int ListReadCount { get; private set; }
            public int ListReplaceCount { get; private set; }
            public int ListReplaceAttemptCount { get; private set; }
            public int ListReplaceConflictsRemaining { get; set; }
            public int ChannelReadManyCount { get; private set; }
            public int ChannelReplaceAttemptCount { get; private set; }
            public int ChannelReplaceConflictsRemaining { get; set; }
            public CosmosException DeleteListException { get; set; }

            public CosmosListDocument GetList(Guid id) => Clone(_lists[id.ToString("D")]);

            public void PutChannel(CosmosChannelDocument document)
            {
                _channels[document.Id] = Clone(document);
                _versions[document.Id] = 1;
            }

            public Task<CosmosItem<CosmosListDocument>> CreateListAsync(
                CosmosListDocument document,
                int retryCount,
                CancellationToken cancellationToken)
            {
                _lists.Add(document.Id, Clone(document));
                _versions[document.Id] = 1;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task<CosmosItem<CosmosListDocument>> ReadListAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ListReadCount++;
                return Task.FromResult(_lists.TryGetValue(id, out var document)
                    ? Item(Clone(document), id)
                    : null);
            }

            public Task<CosmosItem<CosmosListDocument>> ReplaceListAsync(
                CosmosListDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ListReplaceAttemptCount++;
                if (ListReplaceConflictsRemaining-- > 0)
                {
                    throw Conflict();
                }

                Assert.Equal(CurrentEtag(document.Id), etag);
                _lists[document.Id] = Clone(document);
                _versions[document.Id]++;
                ListReplaceCount++;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task DeleteListAsync(string id, CancellationToken cancellationToken)
            {
                if (DeleteListException != null)
                {
                    throw DeleteListException;
                }

                _lists.Remove(id);
                return Task.CompletedTask;
            }

            public Task<CosmosItem<CosmosChannelDocument>> CreateChannelAsync(
                CosmosChannelDocument document,
                int retryCount,
                CancellationToken cancellationToken)
            {
                _channels.Add(document.Id, Clone(document));
                _versions[document.Id] = 1;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            public Task<CosmosItem<CosmosChannelDocument>> ReadChannelAsync(
                string id,
                int retryCount,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_channels.TryGetValue(id, out var document)
                    ? Item(Clone(document), id)
                    : null);
            }

            public Task<IReadOnlyList<CosmosChannelDocument>> ReadChannelsAsync(
                IReadOnlyCollection<string> ids,
                CancellationToken cancellationToken)
            {
                ChannelReadManyCount++;
                IReadOnlyList<CosmosChannelDocument> result = ids
                    .Where(_channels.ContainsKey)
                    .Select(id => Clone(_channels[id]))
                    .ToArray();
                return Task.FromResult(result);
            }

            public Task<CosmosItem<CosmosChannelDocument>> ReplaceChannelAsync(
                CosmosChannelDocument document,
                string etag,
                int retryCount,
                CancellationToken cancellationToken)
            {
                ChannelReplaceAttemptCount++;
                if (ChannelReplaceConflictsRemaining-- > 0)
                {
                    throw Conflict();
                }

                Assert.Equal(CurrentEtag(document.Id), etag);
                _channels[document.Id] = Clone(document);
                _versions[document.Id]++;
                return Task.FromResult(Item(Clone(document), document.Id));
            }

            private CosmosItem<T> Item<T>(T resource, string id)
            {
                return new CosmosItem<T>(resource, CurrentEtag(id));
            }

            private string CurrentEtag(string id) => $"etag-{_versions[id]}";

            private static T Clone<T>(T value)
            {
                var stream = CosmosSystemTextJsonSerializer.Instance.ToStream(value);
                return CosmosSystemTextJsonSerializer.Instance.FromStream<T>(stream);
            }

            private static CosmosException Conflict()
            {
                return new CosmosException(
                    "Concurrency conflict.",
                    HttpStatusCode.PreconditionFailed,
                    subStatusCode: 0,
                    activityId: string.Empty,
                    requestCharge: 0);
            }
        }
    }
}
