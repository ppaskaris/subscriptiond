using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(LocalDbTestFixture.CollectionName)]
    [Trait("Category", "LocalDb")]
    public sealed class SqlWorkerStateProviderContractTests : WorkerStateProviderContractTests
    {
        public SqlWorkerStateProviderContractTests(LocalDbTestFixture fixture)
            : base(new SqlProviderContractTestFixture(fixture))
        {
        }

        [LocalDbFact]
        public Task GetOrCreate() => GetOrCreateContractAsync();

        [LocalDbFact]
        public Task ForceChannelRefresh() => ForceChannelRefreshContractAsync();

        [LocalDbFact]
        public Task CompleteChannelRefreshPass() => CompleteChannelRefreshPassContractAsync();

        [LocalDbFact]
        public Task ChannelRefreshCompletionProtectsNewForce() =>
            ChannelRefreshCompletionProtectsNewForceContractAsync();

        [LocalDbFact]
        public Task ChannelRefreshCompletionProtectsNewerSchedule() =>
            ChannelRefreshCompletionProtectsNewerScheduleContractAsync();

        [LocalDbFact]
        public Task ChannelRefreshCompletionProtectsRepeatedForce() =>
            ChannelRefreshCompletionProtectsRepeatedForceContractAsync();

        [LocalDbFact]
        public Task CompletePurge() => CompletePurgeContractAsync();
    }
}
