using System;

namespace youtubed.Services
{
    public interface IYoutubeRetryAfterObservation : IDisposable
    {
        TimeSpan? GetDelay(TimeProvider timeProvider);
    }

    public interface IYoutubeRetryAfterProvider
    {
        IYoutubeRetryAfterObservation BeginObservation();
    }
}
