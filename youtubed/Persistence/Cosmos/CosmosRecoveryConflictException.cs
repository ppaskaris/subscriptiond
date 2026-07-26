using System;

namespace youtubed.Persistence.Cosmos
{
    internal sealed class CosmosRecoveryConflictException : Exception
    {
        internal CosmosRecoveryConflictException(string message)
            : base(message)
        {
        }
    }
}
