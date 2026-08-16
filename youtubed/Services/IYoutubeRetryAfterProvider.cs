using System;

namespace youtubed.Services
{
    public interface IYoutubeRetryAfterProvider
    {
        TimeSpan? ConsumeRetryAfter();
    }
}
