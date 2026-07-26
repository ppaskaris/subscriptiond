using System;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence
{
    public sealed record ConsistencyRecoveryPassBudget(
        int PageSize,
        int MaxItems,
        double RuSchedulingBudget)
    {
        public static ConsistencyRecoveryPassBudget Default { get; } = new(
            Constants.ConsistencyRecoveryBatchSize,
            Constants.ConsistencyRecoveryMaxItemsPerPass,
            Constants.ConsistencyRecoveryRuBudgetPerPass);

        public void Validate()
        {
            if (PageSize is < 1 or > Constants.ConsistencyRecoveryBatchSize)
            {
                throw new ArgumentOutOfRangeException(nameof(PageSize));
            }

            if (MaxItems is < 1 or > Constants.ConsistencyRecoveryMaxItemsPerPass)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxItems));
            }

            if (RuSchedulingBudget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(RuSchedulingBudget));
            }
        }
    }

    public sealed record ConsistencyRecoveryPassResult(
        int Examined,
        int Claimed,
        int Succeeded,
        int Failed,
        int Poison,
        double RequestCharge,
        bool HasMoreEligibleWork,
        DateTimeOffset? NextEligibleAt)
    {
        public static ConsistencyRecoveryPassResult Empty { get; } =
            new(0, 0, 0, 0, 0, 0, false, null);
    }

    public interface IConsistencyRecoveryService
    {
        Task<ConsistencyRecoveryPassResult> RecoverAsync(
            ConsistencyRecoveryPassBudget budget,
            CancellationToken cancellationToken);
    }
}
