using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using youtubed.Domain;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosShareLinkRepositoryIntegrationTests
    {
        private readonly CosmosTestFixture _fixture;

        public CosmosShareLinkRepositoryIntegrationTests(CosmosTestFixture fixture)
        {
            _fixture = fixture;
        }

        [CosmosFact]
        public async Task CompetingServiceConsumersReturnTheListTokenExactlyOnce()
        {
            var clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)
            };
            var repository = CreateRepository(clock);
            var lists = new CosmosListRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosListRepository>.Instance);
            var service = new ShareLinkService(repository, lists, clock);
            var listId = Guid.NewGuid();
            var token = Enumerable.Repeat((byte)37, 40).ToArray();
            await CreateListAsync(listId, token, clock.UtcNow);
            var link = new ShareLink
            {
                Password = $"compete-{Guid.NewGuid():N}",
                ListId = listId,
                CreatedAt = clock.UtcNow,
                ExpiresAfter = clock.UtcNow.AddHours(1)
            };
            Assert.True(await repository.TryCreateAsync(link));

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;
            async Task<youtubed.Models.ConsumedShareLinkModel> ConsumeAsync()
            {
                if (System.Threading.Interlocked.Increment(ref readyCount) == 2)
                {
                    ready.SetResult();
                }

                await release.Task;
                return await service.ConsumeShareLinkAsync(link.Password);
            }

            var firstTask = Task.Run(ConsumeAsync);
            var secondTask = Task.Run(ConsumeAsync);
            await ready.Task;
            release.SetResult();
            var consumed = await Task.WhenAll(firstTask, secondTask);

            var winner = Assert.Single(consumed, result => result != null);
            Assert.Equal(listId, winner.ListId);
            Assert.Equal(token, winner.Token);
            Assert.Single(consumed, result => result == null);
        }

        [CosmosFact]
        public async Task ShareDocumentNeverStoresListTokenAndRecomputesTtlOnConsume()
        {
            var clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)
            };
            var repository = CreateRepository(clock);
            var listId = Guid.NewGuid();
            var token = Enumerable.Repeat((byte)91, 40).ToArray();
            await CreateListAsync(listId, token, clock.UtcNow);
            var link = new ShareLink
            {
                Password = $"shape-{Guid.NewGuid():N}",
                ListId = listId,
                CreatedAt = clock.UtcNow,
                ExpiresAfter = clock.UtcNow.AddHours(1)
            };
            Assert.True(await repository.TryCreateAsync(link));

            clock.UtcNow = clock.UtcNow.AddMinutes(10);
            Assert.True(await repository.TryMarkUsedAsync(
                link.Password,
                listId,
                clock.UtcNow));
            using var response = await _fixture.Context.ShareLinks.ReadItemStreamAsync(
                link.Password,
                new PartitionKey(link.Password));
            using var json = await JsonDocument.ParseAsync(response.Content);

            Assert.False(json.RootElement.TryGetProperty("token", out _));
            Assert.Equal(
                CosmosDocumentMapper.GetTtlSeconds(
                    link.ExpiresAfter + Constants.ShareLinkRetentionAfterExpiration,
                    clock.UtcNow),
                json.RootElement.GetProperty("ttl").GetInt32());
        }

        [CosmosFact]
        public async Task MissingTargetListLeavesShareUnused()
        {
            var clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)
            };
            var repository = CreateRepository(clock);
            var lists = new CosmosListRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosListRepository>.Instance);
            var service = new ShareLinkService(repository, lists, clock);
            var link = new ShareLink
            {
                Password = $"missing-list-{Guid.NewGuid():N}",
                ListId = Guid.NewGuid(),
                CreatedAt = clock.UtcNow,
                ExpiresAfter = clock.UtcNow.AddHours(1)
            };
            Assert.True(await repository.TryCreateAsync(link));

            var result = await service.ConsumeShareLinkAsync(link.Password);

            Assert.Null(result);
            Assert.Null((await repository.GetAsync(link.Password)).UsedAt);
        }

        [CosmosFact]
        public async Task RestartAfterCommittedUseCannotRevealToken()
        {
            var clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero)
            };
            var repository = CreateRepository(clock);
            var lists = new CosmosListRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosListRepository>.Instance);
            var listId = Guid.NewGuid();
            await CreateListAsync(
                listId,
                Enumerable.Repeat((byte)53, 40).ToArray(),
                clock.UtcNow);
            var link = new ShareLink
            {
                Password = $"restart-{Guid.NewGuid():N}",
                ListId = listId,
                CreatedAt = clock.UtcNow,
                ExpiresAfter = clock.UtcNow.AddHours(1)
            };
            Assert.True(await repository.TryCreateAsync(link));
            Assert.True(await repository.TryMarkUsedAsync(
                link.Password,
                link.ListId,
                clock.UtcNow));

            var restartedService = new ShareLinkService(repository, lists, clock);
            var result = await restartedService.ConsumeShareLinkAsync(link.Password);

            Assert.Null(result);
        }

        [CosmosFact]
        public async Task ShareLinkTtlPhysicallyDeletesDocumentWithoutRepairWork()
        {
            var id = $"ttl-{Guid.NewGuid():N}";
            await _fixture.Context.ShareLinks.CreateItemAsync(
                new CosmosShareLinkDocument
                {
                    Id = id,
                    ListId = Guid.NewGuid().ToString("D"),
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAfter = DateTimeOffset.UtcNow,
                    Ttl = 1
                },
                new PartitionKey(id));

            var deleted = false;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (!deleted && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                try
                {
                    await _fixture.Context.ShareLinks.ReadItemAsync<CosmosShareLinkDocument>(
                        id,
                        new PartitionKey(id));
                }
                catch (CosmosException exception) when (
                    exception.StatusCode == HttpStatusCode.NotFound)
                {
                    deleted = true;
                }
            }

            Assert.True(
                deleted,
                "The emulator did not physically delete the TTL-expired share link within three minutes.");
        }

        private CosmosShareLinkRepository CreateRepository(FakeAppClock clock)
        {
            return new CosmosShareLinkRepository(
                _fixture.Context,
                clock,
                NullLogger<CosmosShareLinkRepository>.Instance);
        }

        private Task CreateListAsync(Guid id, byte[] token, DateTimeOffset now)
        {
            var document = new CosmosListDocument
            {
                Id = id.ToString("D"),
                Token = token,
                Title = "Share list",
                PlaybackRate = 1m,
                ExpiredAfter = now.AddDays(45),
                ChannelIds = Array.Empty<string>(),
                Ttl = (int)TimeSpan.FromDays(45).TotalSeconds
            };
            return _fixture.Context.Lists.CreateItemAsync(
                document,
                new PartitionKey(document.Id));
        }
    }
}
