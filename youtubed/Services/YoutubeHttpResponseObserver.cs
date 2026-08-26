using Google.Apis.Http;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class YoutubeHttpResponseObserver :
        IConfigurableHttpClientInitializer,
        IHttpUnsuccessfulResponseHandler,
        IYoutubeRetryAfterProvider
    {
        private readonly AsyncLocal<Observation> _current = new();

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

        public IYoutubeRetryAfterObservation BeginObservation()
        {
            var observation = new Observation(this, _current.Value);
            _current.Value = observation;
            return observation;
        }

        internal void Capture(HttpResponseMessage response)
        {
            var retryAfter = response?.Headers?.RetryAfter;
            _current.Value?.Capture(retryAfter?.Delta, retryAfter?.Date);
        }

        private sealed class Observation : IYoutubeRetryAfterObservation
        {
            private readonly YoutubeHttpResponseObserver _owner;
            private readonly Observation _previous;
            private TimeSpan? _delta;
            private DateTimeOffset? _date;
            private bool _disposed;

            public Observation(YoutubeHttpResponseObserver owner, Observation previous)
            {
                _owner = owner;
                _previous = previous;
            }

            public void Capture(TimeSpan? delta, DateTimeOffset? date)
            {
                _delta = delta;
                _date = date;
            }

            public TimeSpan? GetDelay(TimeProvider timeProvider)
            {
                ArgumentNullException.ThrowIfNull(timeProvider);
                if (_delta.HasValue)
                {
                    return _delta.Value > TimeSpan.Zero ? _delta.Value : TimeSpan.Zero;
                }
                if (_date.HasValue)
                {
                    var delay = _date.Value - timeProvider.GetUtcNow();
                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
                return null;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                if (ReferenceEquals(_owner._current.Value, this))
                {
                    _owner._current.Value = _previous;
                }
            }
        }
    }
}
