using Google.Apis.Http;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class YoutubeHttpResponseObserver :
        IConfigurableHttpClientInitializer,
        IHttpUnsuccessfulResponseHandler,
        IYoutubeRetryAfterProvider
    {
        private readonly object _sync = new object();
        private TimeSpan? _retryAfter;

        public void Initialize(ConfigurableHttpClient httpClient)
        {
            ArgumentNullException.ThrowIfNull(httpClient);
            httpClient.MessageHandler.NumTries = 1;
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
        }

        public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            Capture(args?.Response);
            return Task.FromResult(false);
        }

        public TimeSpan? ConsumeRetryAfter()
        {
            lock (_sync)
            {
                var value = _retryAfter;
                _retryAfter = null;
                return value;
            }
        }

        internal void Capture(HttpResponseMessage response)
        {
            var retry = response?.Headers?.RetryAfter;
            var value = retry?.Delta;
            if (!value.HasValue && retry?.Date != null)
            {
                var delay = retry.Date.Value - DateTimeOffset.UtcNow;
                value = delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            lock (_sync)
            {
                _retryAfter = value;
            }
        }
    }
}
