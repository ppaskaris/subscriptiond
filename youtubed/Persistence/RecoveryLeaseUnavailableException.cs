using System;

namespace youtubed.Persistence
{
    public sealed class RecoveryLeaseUnavailableException : Exception
    {
        public RecoveryLeaseUnavailableException(string message)
            : base(message)
        {
        }
    }
}
