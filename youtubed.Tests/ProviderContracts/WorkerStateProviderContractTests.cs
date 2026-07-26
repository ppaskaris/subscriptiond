using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace youtubed.Tests.ProviderContracts
{
    public abstract class WorkerStateProviderContractTests : ProviderContractTestBase
    {
        protected WorkerStateProviderContractTests(IProviderContractTestFixture fixture)
            : base(fixture)
        {
        }

        protected async Task GetOrCreateContractAsync()
        {
            var created = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            Clock.UtcNow = Clock.UtcNow.AddHours(1);
            var existing = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DefaultNow, created.NextChannelRefreshAt);
            Assert.Equal(0, created.ChannelRefreshForceCount);
            Assert.Equal(DefaultNow, created.NextPurgeAt);
            Assert.Equal(DefaultNow, created.NextConsistencyRecoveryAt);
            Assert.Equal(0, created.ConsistencyRecoveryForceCount);
            Assert.Equal(created.NextChannelRefreshAt, existing.NextChannelRefreshAt);
            Assert.Equal(created.ChannelRefreshForceCount, existing.ChannelRefreshForceCount);
            Assert.Equal(created.NextPurgeAt, existing.NextPurgeAt);
            Assert.Equal(created.NextConsistencyRecoveryAt, existing.NextConsistencyRecoveryAt);
            Assert.Equal(
                created.ConsistencyRecoveryForceCount,
                existing.ConsistencyRecoveryForceCount);
        }

        protected async Task ForceChannelRefreshContractAsync()
        {
            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);
            var firstForce = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, firstForce.NextChannelRefreshAt);
            Assert.Equal(1, firstForce.ChannelRefreshForceCount);
            Assert.Equal(DefaultNow, firstForce.NextPurgeAt);

            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);
            var secondForce = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, secondForce.NextChannelRefreshAt);
            Assert.Equal(2, secondForce.ChannelRefreshForceCount);
            Assert.Equal(firstForce.NextPurgeAt, secondForce.NextPurgeAt);
        }

        protected async Task CompleteChannelRefreshPassContractAsync()
        {
            var observed = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                null,
                CancellationToken.None);
            var withoutKnownWork = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Null(withoutKnownWork.NextChannelRefreshAt);

            var nextRefresh = Clock.UtcNow.AddMinutes(15);
            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                withoutKnownWork.NextChannelRefreshAt,
                withoutKnownWork.ChannelRefreshForceCount,
                nextRefresh,
                CancellationToken.None);
            var scheduled = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(nextRefresh, scheduled.NextChannelRefreshAt);
            Assert.Equal(observed.ChannelRefreshForceCount, scheduled.ChannelRefreshForceCount);
            Assert.Equal(observed.NextPurgeAt, scheduled.NextPurgeAt);
        }

        protected async Task ChannelRefreshCompletionProtectsNewForceContractAsync()
        {
            var observed = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);

            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                Clock.UtcNow.AddMinutes(30),
                CancellationToken.None);
            var protectedState = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, protectedState.NextChannelRefreshAt);
            Assert.Equal(observed.ChannelRefreshForceCount + 1, protectedState.ChannelRefreshForceCount);
        }

        protected async Task ChannelRefreshCompletionProtectsNewerScheduleContractAsync()
        {
            var original = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            var newerSchedule = Clock.UtcNow.AddMinutes(15);
            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                original.NextChannelRefreshAt,
                original.ChannelRefreshForceCount,
                newerSchedule,
                CancellationToken.None);

            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                original.NextChannelRefreshAt,
                original.ChannelRefreshForceCount,
                Clock.UtcNow.AddMinutes(30),
                CancellationToken.None);
            var protectedState = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(newerSchedule, protectedState.NextChannelRefreshAt);
            Assert.Equal(original.ChannelRefreshForceCount, protectedState.ChannelRefreshForceCount);
        }

        protected async Task ChannelRefreshCompletionProtectsRepeatedForceContractAsync()
        {
            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);
            var observed = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);

            await Provider.WorkerState.CompleteChannelRefreshPassAsync(
                observed.NextChannelRefreshAt,
                observed.ChannelRefreshForceCount,
                null,
                CancellationToken.None);
            var protectedState = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, protectedState.NextChannelRefreshAt);
            Assert.Equal(observed.ChannelRefreshForceCount + 1, protectedState.ChannelRefreshForceCount);
        }

        protected async Task CompletePurgeContractAsync()
        {
            await Provider.WorkerState.ForceChannelRefreshAsync(CancellationToken.None);
            var nextPurge = Clock.UtcNow.AddMinutes(10);

            await Provider.WorkerState.CompletePurgeAsync(nextPurge, CancellationToken.None);
            var completed = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, completed.NextChannelRefreshAt);
            Assert.Equal(1, completed.ChannelRefreshForceCount);
            Assert.Equal(nextPurge, completed.NextPurgeAt);
        }

        protected async Task ForceConsistencyRecoveryContractAsync()
        {
            await Provider.WorkerState.ForceConsistencyRecoveryAsync(CancellationToken.None);
            var first = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            Assert.Equal(DateTimeOffset.MinValue, first.NextConsistencyRecoveryAt);
            Assert.Equal(1, first.ConsistencyRecoveryForceCount);

            await Provider.WorkerState.ForceConsistencyRecoveryAsync(CancellationToken.None);
            var second = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            Assert.Equal(DateTimeOffset.MinValue, second.NextConsistencyRecoveryAt);
            Assert.Equal(2, second.ConsistencyRecoveryForceCount);
        }

        protected async Task RecoveryCompletionProtectsForceGenerationContractAsync()
        {
            var observed = await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);
            await Provider.WorkerState.ForceConsistencyRecoveryAsync(CancellationToken.None);
            await Provider.WorkerState.CompleteConsistencyRecoveryPassAsync(
                observed.NextConsistencyRecoveryAt,
                observed.ConsistencyRecoveryForceCount,
                Clock.UtcNow.AddMinutes(10),
                CancellationToken.None);
            var protectedState =
                await Provider.WorkerState.GetOrCreateAsync(CancellationToken.None);

            Assert.Equal(DateTimeOffset.MinValue, protectedState.NextConsistencyRecoveryAt);
            Assert.Equal(
                observed.ConsistencyRecoveryForceCount + 1,
                protectedState.ConsistencyRecoveryForceCount);
        }
    }
}
