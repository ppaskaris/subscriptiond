using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Persistence;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class WorkerStateRepositoryIntegrationTests : LocalDbIntegrationTestBase
    {
        private readonly FakeAppClock _clock;
        private readonly WorkerStateRepository _repository;

        public WorkerStateRepositoryIntegrationTests(LocalDbTestFixture fixture)
            : base(fixture)
        {
            _clock = new FakeAppClock
            {
                UtcNow = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero)
            };
            _repository = new WorkerStateRepository(fixture.ConnectionFactory, _clock);
        }

        [LocalDbFact]
        public async Task GetOrCreateAsync_CreatesSingletonStateFromClock()
        {
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);
            _clock.UtcNow = _clock.UtcNow.AddHours(1);
            var existing = await _repository.GetOrCreateAsync(CancellationToken.None);
            var rowCount = await ScalarAsync<int>("SELECT COUNT(*) FROM WorkerState;");

            Assert.Equal(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero), state.NextChannelRefreshAt);
            Assert.Equal(0, state.ChannelRefreshForceCount);
            Assert.Equal(new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero), state.NextPurgeAt);
            Assert.Equal(state.NextChannelRefreshAt, existing.NextChannelRefreshAt);
            Assert.Equal(state.ChannelRefreshForceCount, existing.ChannelRefreshForceCount);
            Assert.Equal(state.NextPurgeAt, existing.NextPurgeAt);
            Assert.Equal(1, rowCount);
        }

        [LocalDbFact]
        public async Task ForceChannelRefreshAsync_CreatesStateAndForcesImmediateRefresh()
        {
            await _repository.ForceChannelRefreshAsync(CancellationToken.None);

            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, state.NextChannelRefreshAt);
            Assert.Equal(1, state.ChannelRefreshForceCount);
            Assert.Equal(_clock.UtcNow, state.NextPurgeAt);
        }

        [LocalDbFact]
        public async Task CompleteChannelRefreshPassAsync_UpdatesWhenObservedStateMatches()
        {
            var observed = await _repository.GetOrCreateAsync(CancellationToken.None);
            var next = _clock.UtcNow.AddMinutes(15);

            await _repository.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                next,
                CancellationToken.None);
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(next, state.NextChannelRefreshAt);
        }

        [LocalDbFact]
        public async Task CompleteChannelRefreshPassAsync_DoesNotOverwriteForcedRefresh()
        {
            var observed = await _repository.GetOrCreateAsync(CancellationToken.None);
            await _repository.ForceChannelRefreshAsync(CancellationToken.None);

            await _repository.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                _clock.UtcNow.AddMinutes(30),
                CancellationToken.None);
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, state.NextChannelRefreshAt);
            Assert.Equal(1, state.ChannelRefreshForceCount);
        }

        [LocalDbFact]
        public async Task CompleteChannelRefreshPassAsync_DoesNotOverwriteRepeatedForcedRefresh()
        {
            await _repository.ForceChannelRefreshAsync(CancellationToken.None);
            var observed = await _repository.GetOrCreateAsync(CancellationToken.None);
            await _repository.ForceChannelRefreshAsync(CancellationToken.None);

            await _repository.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                null,
                CancellationToken.None);
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, state.NextChannelRefreshAt);
            Assert.Equal(observed.ChannelRefreshForceCount + 1, state.ChannelRefreshForceCount);
        }

        [LocalDbFact]
        public async Task CompleteChannelRefreshPassAsync_CanSetAndObserveNull()
        {
            var observed = await _repository.GetOrCreateAsync(CancellationToken.None);
            await _repository.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                null,
                CancellationToken.None);

            await _repository.CompleteChannelRefreshPassAsync(
                null,
                observed.ChannelRefreshForceCount,
                _clock.UtcNow.AddMinutes(45),
                CancellationToken.None);
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(_clock.UtcNow.AddMinutes(45), state.NextChannelRefreshAt);
        }

        [LocalDbFact]
        public async Task CompletePurgeAsync_UpdatesNextPurgeOnly()
        {
            await _repository.ForceChannelRefreshAsync(CancellationToken.None);
            var nextPurge = _clock.UtcNow.AddMinutes(10);

            await _repository.CompletePurgeAsync(nextPurge, CancellationToken.None);
            var state = await _repository.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, state.NextChannelRefreshAt);
            Assert.Equal(nextPurge, state.NextPurgeAt);
        }
    }
}
