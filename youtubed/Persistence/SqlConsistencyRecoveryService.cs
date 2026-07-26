using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Persistence
{
    public sealed class SqlConsistencyRecoveryService : IConsistencyRecoveryService
    {
        public Task<ConsistencyRecoveryPassResult> RecoverAsync(
            ConsistencyRecoveryPassBudget budget,
            CancellationToken cancellationToken)
        {
            budget.Validate();
            return Task.FromResult(ConsistencyRecoveryPassResult.Empty);
        }
    }
}
