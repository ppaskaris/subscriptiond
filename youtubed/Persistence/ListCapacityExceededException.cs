using System;

namespace youtubed.Persistence
{
    public sealed class ListCapacityExceededException : InvalidOperationException
    {
        public ListCapacityExceededException(string message)
            : base(message)
        {
        }
    }
}
