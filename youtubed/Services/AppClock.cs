using System;

namespace youtubed.Services
{
    public sealed class AppClock : IAppClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public DateOnly UtcToday => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public TimeSpan RandomDelay(TimeSpan min, TimeSpan max)
        {
            if (max < min)
            {
                throw new ArgumentException("Value is too small.", nameof(max));
            }

            return Constants.RandomlyBetween(min, max);
        }

        public DateTimeOffset UtcNowAfterRandomDelay(TimeSpan min, TimeSpan max)
        {
            return UtcNow.Add(RandomDelay(min, max));
        }
    }
}
