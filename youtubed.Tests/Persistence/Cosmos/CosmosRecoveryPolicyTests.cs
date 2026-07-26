using System;
using Xunit;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;

namespace youtubed.Tests.Persistence.Cosmos
{
    public sealed class CosmosRecoveryPolicyTests
    {
        [Fact]
        public void BudgetRejectsWorkBeyondFixedBounds()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ConsistencyRecoveryPassBudget(26, 100, 2_000).Validate());
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ConsistencyRecoveryPassBudget(25, 101, 2_000).Validate());
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ConsistencyRecoveryPassBudget(25, 100, 0).Validate());
        }

        [Fact]
        public void OptionsRejectUnsafeDocumentAndEdgeBounds()
        {
            Assert.Throws<InvalidOperationException>(() => new CosmosRecoveryOptions
            {
                MaxActiveEdgesPerList = 126
            }.Validate());
            Assert.Throws<InvalidOperationException>(() => new CosmosRecoveryOptions
            {
                RecoveryDocumentSizeCeilingBytes = 16_385
            }.Validate());
            Assert.Throws<InvalidOperationException>(() => new CosmosRecoveryOptions
            {
                ChannelSerializedSizeSafetyCeilingBytes = 1_900_001
            }.Validate());
        }

        [Fact]
        public void EdgeIdsAreDeterministicBoundedAndDoNotExposeChannelIds()
        {
            var channelId = "UC-secret-looking-but-not-a-token";
            var first = CosmosRecoveryStore.GetEdgeId(channelId);
            var second = CosmosRecoveryStore.GetEdgeId(channelId);

            Assert.Equal(first, second);
            Assert.StartsWith("edge:", first);
            Assert.DoesNotContain(channelId, first, StringComparison.Ordinal);
            Assert.Equal(69, first.Length);
        }

        [Fact]
        public async System.Threading.Tasks.Task SqlRecoveryIsBoundedNoWork()
        {
            var result = await new SqlConsistencyRecoveryService().RecoverAsync(
                ConsistencyRecoveryPassBudget.Default,
                default);

            Assert.Equal(ConsistencyRecoveryPassResult.Empty, result);
        }
    }
}
