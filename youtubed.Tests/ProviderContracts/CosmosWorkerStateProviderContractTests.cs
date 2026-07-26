using System.Threading.Tasks;
using Xunit;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.ProviderContracts
{
    [Collection(CosmosTestFixture.CollectionName)]
    [Trait("Category", "Cosmos")]
    public sealed class CosmosWorkerStateProviderContractTests : WorkerStateProviderContractTests
    {
        public CosmosWorkerStateProviderContractTests(CosmosTestFixture fixture)
            : base(new CosmosListProviderContractTestFixture(fixture))
        {
        }

        [CosmosFact]
        public Task GetOrCreate() => GetOrCreateContractAsync();

        [CosmosFact]
        public Task ForceChannelRefresh() => ForceChannelRefreshContractAsync();

        [CosmosFact]
        public Task CompleteChannelRefreshPass() => CompleteChannelRefreshPassContractAsync();

        [CosmosFact]
        public Task ChannelRefreshCompletionProtectsNewForce() =>
            ChannelRefreshCompletionProtectsNewForceContractAsync();

        [CosmosFact]
        public Task ChannelRefreshCompletionProtectsNewerSchedule() =>
            ChannelRefreshCompletionProtectsNewerScheduleContractAsync();

        [CosmosFact]
        public Task ChannelRefreshCompletionProtectsRepeatedForce() =>
            ChannelRefreshCompletionProtectsRepeatedForceContractAsync();

        [CosmosFact]
        public Task CompletePurge() => CompletePurgeContractAsync();

        [CosmosFact]
        public Task ForceConsistencyRecovery() => ForceConsistencyRecoveryContractAsync();

        [CosmosFact]
        public Task RecoveryCompletionProtectsForceGeneration() =>
            RecoveryCompletionProtectsForceGenerationContractAsync();
    }
}
