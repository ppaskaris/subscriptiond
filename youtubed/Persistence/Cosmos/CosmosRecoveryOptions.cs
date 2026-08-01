using System;

namespace youtubed.Persistence.Cosmos
{
    public sealed class CosmosRecoveryOptions
    {
        public const string SectionName = "CosmosRecovery";

        public int PageSize { get; set; } = Constants.ConsistencyRecoveryBatchSize;
        public int MaxItemsPerPass { get; set; } = Constants.ConsistencyRecoveryMaxItemsPerPass;
        public double RuBudgetPerPass { get; set; } = Constants.ConsistencyRecoveryRuBudgetPerPass;
        public TimeSpan PollInterval { get; set; } = Constants.ConsistencyRecoveryPollInterval;
        public TimeSpan LeaseDuration { get; set; } = Constants.ConsistencyRecoveryLeaseDuration;
        public TimeSpan MutationCommitSafetyWindow { get; set; } = TimeSpan.FromSeconds(15);
        public int PoisonAttemptCount { get; set; } = Constants.ConsistencyRecoveryPoisonAttemptCount;
        public int RecoveryDocumentSizeCeilingBytes { get; set; } =
            Constants.RecoveryDocumentSizeCeilingBytes;
        public int MaxActiveEdgesPerList { get; set; } =
            Constants.RecoveryMaxActiveEdgesPerList;
        public int ChannelSerializedSizeSafetyCeilingBytes { get; set; } =
            Constants.CosmosChannelSerializedSizeSafetyCeilingBytes;
        internal TimeSpan ChannelOrphanRetention { get; set; } =
            Constants.ChannelOrphanRetention;

        public void Validate()
        {
            _ = new ConsistencyRecoveryPassBudget(
                PageSize,
                MaxItemsPerPass,
                RuBudgetPerPass);
            new ConsistencyRecoveryPassBudget(
                PageSize,
                MaxItemsPerPass,
                RuBudgetPerPass).Validate();

            if (LeaseDuration <= TimeSpan.Zero
                || MutationCommitSafetyWindow <= TimeSpan.Zero
                || MutationCommitSafetyWindow >= LeaseDuration
                || PollInterval <= TimeSpan.Zero
                || PoisonAttemptCount < 1
                || RecoveryDocumentSizeCeilingBytes > Constants.RecoveryDocumentSizeCeilingBytes
                || RecoveryDocumentSizeCeilingBytes < 1
                || MaxActiveEdgesPerList is < 100 or > Constants.RecoveryMaxActiveEdgesPerList
                || ChannelSerializedSizeSafetyCeilingBytes > Constants.CosmosChannelSerializedSizeSafetyCeilingBytes
                || ChannelOrphanRetention <= TimeSpan.Zero
                || ChannelOrphanRetention > Constants.ChannelOrphanRetention)
            {
                throw new InvalidOperationException("Cosmos recovery options exceed supported bounds.");
            }
        }
    }
}
