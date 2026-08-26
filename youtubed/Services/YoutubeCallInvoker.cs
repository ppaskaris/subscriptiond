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
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public sealed class YoutubeCallInvoker : IYoutubeCallInvoker, IDisposable
    {
        private enum CooldownKind
        {
            None,
            Transient,
            Quota
        }

        private readonly record struct CooldownState(
            CooldownKind Kind,
            TimeSpan Delay,
            DateTimeOffset QuotaDeadline);

        private static readonly Meter Telemetry = new("youtubed.youtube", "1.0");
        private static readonly Counter<long> AdmissionCounter =
            Telemetry.CreateCounter<long>("youtube.request.admissions");
        private static readonly Counter<long> AttemptCounter =
            Telemetry.CreateCounter<long>("youtube.request.attempts");
        private static readonly Counter<long> RetryCounter =
            Telemetry.CreateCounter<long>("youtube.request.retries");
        private static readonly Counter<long> RejectionCounter =
            Telemetry.CreateCounter<long>("youtube.request.rejections");
        private static readonly Counter<long> CooldownCounter =
            Telemetry.CreateCounter<long>("youtube.request.cooldowns");
        private static readonly Counter<long> QuotaCounter =
            Telemetry.CreateCounter<long>("youtube.request.quota_exhaustions");
        private static readonly UpDownCounter<long> ActiveAttemptCounter =
            Telemetry.CreateUpDownCounter<long>("youtube.request.active_attempts");
        private static readonly Histogram<double> AdmissionWaitDuration =
            Telemetry.CreateHistogram<double>("youtube.request.admission_wait.duration", "ms");
        private static readonly Histogram<double> CooldownWaitDuration =
            Telemetry.CreateHistogram<double>("youtube.request.cooldown_wait.duration", "ms");
        private static readonly Histogram<double> AttemptDuration =
            Telemetry.CreateHistogram<double>("youtube.request.attempt.duration", "ms");

        private readonly object _sync = new();
        private readonly YoutubeSyncOptions _options;
        private readonly ILogger<YoutubeCallInvoker> _logger;
        private readonly IYoutubeRetryAfterProvider _retryAfterProvider;
        private readonly TimeProvider _timeProvider;
        private readonly RateLimiter _limiter;
        private readonly bool _ownsLimiter;
        private long _transientCooldownUntilTimestamp;
        private DateTimeOffset _quotaCooldownUntilUtc;

        public YoutubeCallInvoker(
            IOptions<YoutubeSyncOptions> options,
            ILogger<YoutubeCallInvoker> logger,
            IYoutubeRetryAfterProvider retryAfterProvider,
            TimeProvider timeProvider = null)
            : this(options, logger, retryAfterProvider, timeProvider, limiter: null)
        {
        }

        internal YoutubeCallInvoker(
            IOptions<YoutubeSyncOptions> options,
            ILogger<YoutubeCallInvoker> logger,
            IYoutubeRetryAfterProvider retryAfterProvider,
            TimeProvider timeProvider,
            RateLimiter limiter)
        {
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _retryAfterProvider = retryAfterProvider
                ?? throw new ArgumentNullException(nameof(retryAfterProvider));
            _timeProvider = timeProvider ?? TimeProvider.System;
            ValidateOptions(_options);
            _limiter = limiter ?? CreateLimiter(_options);
            _ownsLimiter = limiter == null;
        }

        public async Task<T> InvokeAsync<T>(
            Func<CancellationToken, Task<T>> call,
            YoutubeCallPolicy policy,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            Exception lastException = null;

            for (var attempt = 0; attempt <= _options.TransientRetryCount; attempt++)
            {
                await ObserveCooldownAsync(policy, cancellationToken);

                var admissionStarted = _timeProvider.GetTimestamp();
                var lease = await _limiter.AcquireAsync(1, cancellationToken);
                AdmissionWaitDuration.Record(
                    _timeProvider.GetElapsedTime(admissionStarted, _timeProvider.GetTimestamp()).TotalMilliseconds);
                if (!lease.IsAcquired)
                {
                    lease.Dispose();
                    RejectionCounter.Add(1);
                    throw new YoutubeTransientException(
                        "YouTube request admission was rejected because the local queue is full.",
                        null);
                }

                AdmissionCounter.Add(1);
                var shouldRetry = false;
                try
                {
                    using var observation = _retryAfterProvider.BeginObservation();
                    var started = _timeProvider.GetTimestamp();
                    if (attempt > 0)
                    {
                        RetryCounter.Add(1);
                    }
                    AttemptCounter.Add(1);
                    ActiveAttemptCounter.Add(1);
                    try
                    {
                        var task = call(cancellationToken);
                        if (task == null)
                        {
                            throw new InvalidOperationException(
                                "The YouTube request delegate returned a null task.");
                        }
                        return await task;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (IsQuotaExceeded(exception))
                    {
                        var retryAfter = NextPacificMidnight(_timeProvider.GetUtcNow());
                        QuotaCounter.Add(1);
                        SetQuotaCooldown(retryAfter);
                        _logger.LogError(
                            "YouTube daily quota exhausted; requests paused until {RetryAfter}.",
                            retryAfter);
                        throw new YoutubeQuotaExceededException(retryAfter, exception);
                    }
                    catch (Exception exception) when (IsTransient(exception))
                    {
                        lastException = exception;
                        var delay = GetRetryDelay(observation, attempt);
                        var cooldown = attempt == _options.TransientRetryCount
                            && _options.TransientCooldown > delay
                                ? _options.TransientCooldown
                                : delay;
                        SetTransientCooldown(cooldown);
                        _logger.LogWarning(
                            exception,
                            "Transient YouTube request failure. Attempt={Attempt}; DelayMs={DelayMs}.",
                            attempt + 1,
                            cooldown.TotalMilliseconds);
                        shouldRetry = attempt < _options.TransientRetryCount;
                    }
                    catch (GoogleApiException exception)
                    {
                        throw new YoutubePermanentException(
                            "YouTube rejected the request permanently.",
                            exception);
                    }
                    finally
                    {
                        ActiveAttemptCounter.Add(-1);
                        AttemptDuration.Record(
                            _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp()).TotalMilliseconds);
                    }
                }
                finally
                {
                    lease.Dispose();
                }

                if (!shouldRetry)
                {
                    break;
                }
            }

            throw new YoutubeTransientException(
                "YouTube request failed after the transient retry budget was exhausted.",
                lastException);
        }

        public void Dispose()
        {
            if (_ownsLimiter)
            {
                _limiter.Dispose();
            }
        }

        private static RateLimiter CreateLimiter(YoutubeSyncOptions options)
        {
            var concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = options.MaximumConcurrentRequests,
                QueueLimit = options.QueueCapacity,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
            var tokenBucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 1,
                QueueLimit = options.MaximumConcurrentRequests,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1d / options.RequestsPerSecond),
                TokensPerPeriod = 1,
                AutoReplenishment = true
            });
            return RateLimiter.CreateChained(concurrency, tokenBucket);
        }

        private async Task ObserveCooldownAsync(
            YoutubeCallPolicy policy,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var cooldown = ReadCooldown();
                if (cooldown.Kind == CooldownKind.None)
                {
                    return;
                }
                if (!policy.WaitForCooldown)
                {
                    if (cooldown.Kind == CooldownKind.Quota)
                    {
                        throw new YoutubeQuotaExceededException(cooldown.QuotaDeadline, null);
                    }
                    throw new YoutubeTransientException(
                        "YouTube requests are temporarily cooling down.",
                        null);
                }
                if (cooldown.Delay > TimeSpan.Zero)
                {
                    CooldownWaitDuration.Record(cooldown.Delay.TotalMilliseconds);
                    await Task.Delay(cooldown.Delay, _timeProvider, cancellationToken);
                }
            }
        }

        private CooldownState ReadCooldown()
        {
            lock (_sync)
            {
                var utcNow = _timeProvider.GetUtcNow();
                if (_quotaCooldownUntilUtc > utcNow)
                {
                    return new CooldownState(
                        CooldownKind.Quota,
                        _quotaCooldownUntilUtc - utcNow,
                        _quotaCooldownUntilUtc);
                }
                var now = _timeProvider.GetTimestamp();
                if (_transientCooldownUntilTimestamp > now)
                {
                    return new CooldownState(
                        CooldownKind.Transient,
                        _timeProvider.GetElapsedTime(now, _transientCooldownUntilTimestamp),
                        default);
                }
                return default;
            }
        }

        private void SetTransientCooldown(TimeSpan delay)
        {
            lock (_sync)
            {
                var deadline = AddTimestampTicks(
                    _timeProvider.GetTimestamp(),
                    DurationToTimestampTicks(delay));
                if (deadline > _transientCooldownUntilTimestamp)
                {
                    _transientCooldownUntilTimestamp = deadline;
                }
            }
            CooldownCounter.Add(1);
        }

        private void SetQuotaCooldown(DateTimeOffset until)
        {
            lock (_sync)
            {
                if (until > _quotaCooldownUntilUtc)
                {
                    _quotaCooldownUntilUtc = until;
                }
            }
            CooldownCounter.Add(1);
        }

        private TimeSpan GetRetryDelay(IYoutubeRetryAfterObservation observation, int attempt)
        {
            var retryAfter = observation.GetDelay(_timeProvider);
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

        private long DurationToTimestampTicks(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                return 0;
            }
            return Math.Max(
                1,
                (long)Math.Ceiling(duration.TotalSeconds * _timeProvider.TimestampFrequency));
        }

        private static long AddTimestampTicks(long timestamp, long ticks) =>
            ticks > long.MaxValue - timestamp ? long.MaxValue : timestamp + ticks;

        private static bool IsQuotaExceeded(Exception exception) =>
            Reasons(exception).Contains("quotaExceeded", StringComparer.OrdinalIgnoreCase);

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

        private static string[] Reasons(Exception exception) =>
            exception is GoogleApiException googleException
                ? googleException.Error?.Errors?
                    .Select(error => error.Reason)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .ToArray() ?? Array.Empty<string>()
                : Array.Empty<string>();

        private static DateTimeOffset NextPacificMidnight(DateTimeOffset now)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Pacific Standard Time" : "America/Los_Angeles");
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            var nextDate = localNow.Date.AddDays(1);
            var unspecified = DateTime.SpecifyKind(nextDate, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        }

        private static void ValidateOptions(YoutubeSyncOptions options)
        {
            if (options.MaximumConcurrentRequests <= 0 || options.QueueCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "YouTube concurrency must be positive and queue capacity cannot be negative.");
            }
            if (!double.IsFinite(options.RequestsPerSecond) || options.RequestsPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "YouTube request rate must be positive.");
            }
            var replenishmentPeriod = TimeSpan.FromSeconds(1d / options.RequestsPerSecond);
            if (replenishmentPeriod <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "YouTube request rate is too high.");
            }
            if (options.TransientRetryCount < 0
                || options.InitialRetryDelay < TimeSpan.Zero
                || options.MaximumRetryDelay < TimeSpan.Zero
                || options.MaximumRetryJitterMilliseconds < 0
                || options.TransientCooldown < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "YouTube retry settings cannot be negative.");
            }
        }
    }
}
