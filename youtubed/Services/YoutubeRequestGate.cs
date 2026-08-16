using Google;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class YoutubeRequestGate : IYoutubeRequestGate, IDisposable
    {
        private static readonly Meter Telemetry = new Meter("youtubed.youtube", "1.0");
        private static readonly Counter<long> RequestAttemptCounter =
            Telemetry.CreateCounter<long>("youtube.request.attempts");
        private static readonly Counter<long> RetryCounter =
            Telemetry.CreateCounter<long>("youtube.request.retries");
        private static readonly Counter<long> ThrottledWaitCounter =
            Telemetry.CreateCounter<long>("youtube.request.throttled_waits");
        private static readonly Histogram<double> ThrottledWaitDuration =
            Telemetry.CreateHistogram<double>("youtube.request.throttled_wait.duration", "ms");
        private static readonly Counter<long> CooldownCounter =
            Telemetry.CreateCounter<long>("youtube.request.cooldowns");
        private static readonly Counter<long> QuotaExhaustionCounter =
            Telemetry.CreateCounter<long>("youtube.request.quota_exhaustions");

        private readonly SemaphoreSlim _concurrency = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly YoutubeSyncOptions _options;
        private readonly ILogger<YoutubeRequestGate> _logger;
        private readonly IYoutubeRetryAfterProvider _retryAfterProvider;
        private DateTimeOffset _nextStart;
        private DateTimeOffset _cooldownUntil;
        private DateTimeOffset _quotaCooldownUntil;
        private long _requestAttempts;
        private long _retries;
        private long _throttledWaits;
        private long _cooldowns;
        private long _quotaExhaustions;

        public YoutubeRequestGate(
            IOptions<YoutubeSyncOptions> options,
            ILogger<YoutubeRequestGate> logger,
            IYoutubeRetryAfterProvider retryAfterProvider)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryAfterProvider = retryAfterProvider
                ?? throw new ArgumentNullException(nameof(retryAfterProvider));
            if (_options.RequestsPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "YouTube request rate must be positive.");
            }
            if (_options.TransientRetryCount < 0
                || _options.InitialRetryDelay < TimeSpan.Zero
                || _options.MaximumRetryDelay < TimeSpan.Zero
                || _options.MaximumRetryJitterMilliseconds < 0
                || _options.TransientCooldown < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "YouTube retry settings cannot be negative.");
            }
        }

        public YoutubeRequestGateSnapshot Snapshot => new YoutubeRequestGateSnapshot(
            Interlocked.Read(ref _requestAttempts),
            Interlocked.Read(ref _retries),
            Interlocked.Read(ref _throttledWaits),
            Interlocked.Read(ref _cooldowns),
            Interlocked.Read(ref _quotaExhaustions));

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> request,
            bool waitForCooldown,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!waitForCooldown)
            {
                ThrowIfCoolingDown();
            }
            Exception lastException = null;
            for (var attempt = 0; attempt <= _options.TransientRetryCount; attempt++)
            {
                await WaitForTurnAsync(waitForCooldown, cancellationToken);
                if (attempt > 0)
                {
                    Interlocked.Increment(ref _retries);
                    RetryCounter.Add(1);
                }
                try
                {
                    Interlocked.Increment(ref _requestAttempts);
                    RequestAttemptCounter.Add(1);
                    var result = await request(cancellationToken);
                    _concurrency.Release();
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _concurrency.Release();
                    throw;
                }
                catch (Exception exception) when (IsQuotaExceeded(exception))
                {
                    var retryAfter = NextPacificMidnight(DateTimeOffset.UtcNow);
                    Interlocked.Increment(ref _quotaExhaustions);
                    QuotaExhaustionCounter.Add(1);
                    _retryAfterProvider.ConsumeRetryAfter();
                    SetCooldown(retryAfter, quotaExhausted: true);
                    _logger.LogError(
                        "YouTube daily quota exhausted; requests paused until {RetryAfter}.",
                        retryAfter);
                    throw new YoutubeQuotaExceededException(retryAfter, exception);
                }
                catch (Exception exception) when (IsTransient(exception))
                {
                    lastException = exception;
                    var delay = GetRetryDelay(attempt);
                    var cooldown = attempt == _options.TransientRetryCount
                        && _options.TransientCooldown > delay
                            ? _options.TransientCooldown
                            : delay;
                    SetCooldown(DateTimeOffset.UtcNow.Add(cooldown), quotaExhausted: false);
                    _logger.LogWarning(
                        exception,
                        "Transient YouTube request failure. Attempt={Attempt}; DelayMs={DelayMs}.",
                        attempt + 1,
                        cooldown.TotalMilliseconds);
                    if (attempt == _options.TransientRetryCount)
                    {
                        break;
                    }
                }
                catch (GoogleApiException exception)
                {
                    _retryAfterProvider.ConsumeRetryAfter();
                    _concurrency.Release();
                    throw new YoutubePermanentException("YouTube rejected the request permanently.", exception);
                }
                catch
                {
                    _retryAfterProvider.ConsumeRetryAfter();
                    _concurrency.Release();
                    throw;
                }
            }

            throw new YoutubeTransientException(
                "YouTube request failed after the transient retry budget was exhausted.",
                lastException);
        }

        public void Dispose()
        {
            _concurrency.Dispose();
        }

        private async Task WaitForTurnAsync(
            bool waitForCooldown,
            CancellationToken cancellationToken)
        {
            await _concurrency.WaitAsync(cancellationToken);
            try
            {
                DateTimeOffset allowedAt;
                DateTimeOffset cooldownUntil;
                DateTimeOffset quotaUntil;
                lock (_sync)
                {
                    allowedAt = _nextStart > _cooldownUntil ? _nextStart : _cooldownUntil;
                    cooldownUntil = _cooldownUntil;
                    quotaUntil = _quotaCooldownUntil;
                }

                if (!waitForCooldown && cooldownUntil > DateTimeOffset.UtcNow)
                {
                    if (quotaUntil > DateTimeOffset.UtcNow)
                    {
                        throw new YoutubeQuotaExceededException(quotaUntil, null);
                    }

                    throw new YoutubeTransientException(
                        "YouTube requests are temporarily cooling down.",
                        null);
                }

                var delay = allowedAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    Interlocked.Increment(ref _throttledWaits);
                    ThrottledWaitCounter.Add(1);
                    ThrottledWaitDuration.Record(delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }

                lock (_sync)
                {
                    _nextStart = DateTimeOffset.UtcNow.AddSeconds(1d / _options.RequestsPerSecond);
                }
            }
            catch
            {
                _concurrency.Release();
                throw;
            }
        }

        private void ThrowIfCoolingDown()
        {
            DateTimeOffset cooldownUntil;
            DateTimeOffset quotaUntil;
            lock (_sync)
            {
                cooldownUntil = _cooldownUntil;
                quotaUntil = _quotaCooldownUntil;
            }

            if (cooldownUntil <= DateTimeOffset.UtcNow)
            {
                return;
            }

            if (quotaUntil > DateTimeOffset.UtcNow)
            {
                throw new YoutubeQuotaExceededException(quotaUntil, null);
            }

            throw new YoutubeTransientException(
                "YouTube requests are temporarily cooling down.",
                null);
        }

        private void SetCooldown(DateTimeOffset until, bool quotaExhausted)
        {
            lock (_sync)
            {
                if (until > _cooldownUntil)
                {
                    _cooldownUntil = until;
                }
                if (quotaExhausted && until > _quotaCooldownUntil)
                {
                    _quotaCooldownUntil = until;
                }
            }

            Interlocked.Increment(ref _cooldowns);
            CooldownCounter.Add(1);
            _concurrency.Release();
        }

        private static bool IsQuotaExceeded(Exception exception)
        {
            return Reasons(exception).Contains("quotaExceeded", StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsTransient(Exception exception)
        {
            if (exception is HttpRequestException || exception is IOException)
            {
                return true;
            }

            if (exception is not GoogleApiException googleException)
            {
                return false;
            }

            return googleException.HttpStatusCode == HttpStatusCode.TooManyRequests
                || (int)googleException.HttpStatusCode >= 500
                || Reasons(exception).Any(reason =>
                    reason.Equals("rateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                    || reason.Equals("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase));
        }

        private TimeSpan GetRetryDelay(int attempt)
        {
            var retryAfter = _retryAfterProvider.ConsumeRetryAfter();
            if (retryAfter.HasValue)
            {
                return retryAfter.Value;
            }

            var exponentialTicks = Math.Min(
                _options.MaximumRetryDelay.Ticks,
                _options.InitialRetryDelay.Ticks * Math.Pow(2, attempt));
            var jitter = _options.MaximumRetryJitterMilliseconds > 0
                ? Random.Shared.Next(0, _options.MaximumRetryJitterMilliseconds + 1)
                : 0;
            return TimeSpan.FromTicks((long)exponentialTicks)
                .Add(TimeSpan.FromMilliseconds(jitter));
        }

        private static string[] Reasons(Exception exception)
        {
            return exception is GoogleApiException googleException
                ? googleException.Error?.Errors?
                    .Select(error => error.Reason)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .ToArray() ?? Array.Empty<string>()
                : Array.Empty<string>();
        }

        private static DateTimeOffset NextPacificMidnight(DateTimeOffset now)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            var nextDate = localNow.Date.AddDays(1);
            var unspecified = DateTime.SpecifyKind(nextDate, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        }
    }
}
