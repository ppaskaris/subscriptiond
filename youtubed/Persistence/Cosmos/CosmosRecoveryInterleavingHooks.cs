using System;
using System.Threading.Tasks;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosRecoveryInterleavingHooks
    {
        internal Func<CosmosRecoveryEdgeDocument, Task> AfterMutationReservationAsync
        {
            get;
            init;
        }

        internal Func<string, Task> BeforeMembershipWorkAsync { get; init; }

        internal Func<string, Task> BeforeProjectionWorkAsync { get; init; }

        internal Func<CosmosRecoveryEdgeDocument, Task> BeforeMembershipEdgeAsync
        {
            get;
            init;
        }

        internal Func<string, Task> AfterProjectionListWriteAsync { get; init; }

        internal Func<string, Task> BeforeCursorAdvanceAsync { get; init; }
    }
}
