using System;

namespace youtubed.Services
{
    public sealed class YoutubeTransientException : Exception
    {
        public YoutubeTransientException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public sealed class YoutubeQuotaExceededException : Exception
    {
        public YoutubeQuotaExceededException(DateTimeOffset retryAfter, Exception innerException)
            : base("The YouTube daily quota is exhausted.", innerException)
        {
            RetryAfter = retryAfter;
        }

        public DateTimeOffset RetryAfter { get; }
    }

    public sealed class YoutubePermanentException : Exception
    {
        public YoutubePermanentException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
