using System;

namespace youtubed.Persistence
{
    public sealed class RecoveryCapacityExceededException : Exception
    {
        public RecoveryCapacityExceededException(string message)
            : base(message)
        {
        }
    }
}
