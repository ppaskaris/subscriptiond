using System;

namespace youtubed.Services
{
    public interface IAppClock
    {
        DateTimeOffset UtcNow { get; }
        DateOnly UtcToday { get; }
        TimeSpan RandomDelay(TimeSpan min, TimeSpan max);
        DateTimeOffset UtcNowAfterRandomDelay(TimeSpan min, TimeSpan max);
    }
}
