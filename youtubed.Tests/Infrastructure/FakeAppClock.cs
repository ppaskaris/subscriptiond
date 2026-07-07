using System;
using youtubed.Services;

namespace youtubed.Tests.Infrastructure
{
    public sealed class FakeAppClock : IAppClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public TimeSpan? RandomDelayValue { get; set; }

        public DateOnly UtcToday => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public TimeSpan RandomDelay(TimeSpan min, TimeSpan max)
        {
            if (max < min)
            {
                throw new ArgumentException("Value is too small.", nameof(max));
            }

            return RandomDelayValue ?? min;
        }

        public DateTimeOffset UtcNowAfterRandomDelay(TimeSpan min, TimeSpan max)
        {
            return UtcNow.Add(RandomDelay(min, max));
        }
    }
}
