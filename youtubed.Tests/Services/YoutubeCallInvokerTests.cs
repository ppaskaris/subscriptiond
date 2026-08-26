using Google;
using Google.Apis.Requests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.Services
{
    public sealed class YoutubeCallInvokerTests
    {
        [Fact]
        public void Defaults_AreBoundedAndConservative()
        {
            var options = new YoutubeSyncOptions();

            Assert.Equal(4, options.MaximumConcurrentRequests);
            Assert.Equal(10, options.RequestsPerSecond);
            Assert.True(options.QueueCapacity > 0);
        }

        [Fact]
        public async Task InvokeAsync_RetriesTransientFailureAfterCancelableCooldownWithoutHoldingLease()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
            var limiter = new TrackingLimiter();
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions
                {
                    TransientRetryCount = 1,
                    InitialRetryDelay = TimeSpan.FromSeconds(10),
                    MaximumRetryDelay = TimeSpan.FromSeconds(10),
                    MaximumRetryJitterMilliseconds = 0
                },
                time,
                limiter);
            var attempts = 0;

            var invocation = invoker.InvokeAsync(
                _ => ++attempts == 1
                    ? Task.FromException<int>(new HttpRequestException("expected"))
                    : Task.FromResult(42),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None);
            await Task.Yield();

            Assert.Equal(1, attempts);
            Assert.Equal(0, limiter.ActiveLeases);
            time.Advance(TimeSpan.FromSeconds(10));

            Assert.Equal(42, await invocation);
            Assert.Equal(2, attempts);
            Assert.Equal(2, limiter.Acquisitions);
        }

        [Fact]
        public async Task InvokeAsync_ForegroundFailsFastDuringSharedCooldown()
        {
            var time = new FakeTimeProvider();
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions
                {
                    TransientRetryCount = 0,
                    InitialRetryDelay = TimeSpan.FromMinutes(1),
                    MaximumRetryDelay = TimeSpan.FromMinutes(1),
                    MaximumRetryJitterMilliseconds = 0,
                    TransientCooldown = TimeSpan.FromMinutes(1)
                },
                time,
                new TrackingLimiter());
            await Assert.ThrowsAsync<YoutubeTransientException>(() => invoker.InvokeAsync<int>(
                _ => Task.FromException<int>(new HttpRequestException("expected")),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None));
            var foregroundCalls = 0;

            await Assert.ThrowsAsync<YoutubeTransientException>(() => invoker.InvokeAsync(
                _ => Task.FromResult(++foregroundCalls),
                YoutubeCallPolicy.Foreground,
                CancellationToken.None));

            Assert.Equal(0, foregroundCalls);
        }

        [Fact]
        public async Task InvokeAsync_CancellationInterruptsRefreshCooldownWait()
        {
            var time = new FakeTimeProvider();
            var limiter = new TrackingLimiter();
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions
                {
                    TransientRetryCount = 0,
                    InitialRetryDelay = TimeSpan.FromMinutes(10),
                    MaximumRetryDelay = TimeSpan.FromMinutes(10),
                    MaximumRetryJitterMilliseconds = 0,
                    TransientCooldown = TimeSpan.FromMinutes(10)
                },
                time,
                limiter);
            await Assert.ThrowsAsync<YoutubeTransientException>(() => invoker.InvokeAsync<int>(
                _ => Task.FromException<int>(new HttpRequestException("expected")),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            var waiting = invoker.InvokeAsync(
                _ => Task.FromResult(1),
                YoutubeCallPolicy.Refresh,
                cancellation.Token);
            await Task.Yield();

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(1, limiter.Acquisitions);
        }

        [Fact]
        public async Task InvokeAsync_RefreshWaiterObservesCooldownExtensionBeforeAcquiring()
        {
            var time = new FakeTimeProvider();
            var limiter = new TrackingLimiter();
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions
                {
                    TransientRetryCount = 0,
                    InitialRetryDelay = TimeSpan.FromSeconds(10),
                    MaximumRetryDelay = TimeSpan.FromSeconds(10),
                    MaximumRetryJitterMilliseconds = 0,
                    TransientCooldown = TimeSpan.FromSeconds(10)
                },
                time,
                limiter);
            var admittedStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAdmitted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var admitted = invoker.InvokeAsync<int>(async _ =>
            {
                admittedStarted.SetResult();
                await releaseAdmitted.Task;
                throw new HttpRequestException("expected extension");
            }, YoutubeCallPolicy.Refresh, CancellationToken.None);
            await admittedStarted.Task;
            await Assert.ThrowsAsync<YoutubeTransientException>(() => invoker.InvokeAsync<int>(
                _ => Task.FromException<int>(new HttpRequestException("expected initial")),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None));
            var waiterCalls = 0;
            var waiter = invoker.InvokeAsync(
                _ => Task.FromResult(++waiterCalls),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None);

            time.Advance(TimeSpan.FromSeconds(5));
            releaseAdmitted.SetResult();
            await Assert.ThrowsAsync<YoutubeTransientException>(() => admitted);
            time.Advance(TimeSpan.FromSeconds(5));
            await DrainContinuationsAsync();

            Assert.Equal(0, waiterCalls);
            Assert.Equal(2, limiter.Acquisitions);
            time.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(1, await waiter);
            Assert.Equal(3, limiter.Acquisitions);
        }

        [Fact]
        public async Task InvokeAsync_QuotaFailurePublishesPacificResetForForegroundCalls()
        {
            var time = new FakeTimeProvider(
                new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions { TransientRetryCount = 0 },
                time,
                new TrackingLimiter());
            var first = await Assert.ThrowsAsync<YoutubeQuotaExceededException>(() =>
                invoker.InvokeAsync<int>(
                    _ => Task.FromException<int>(GoogleException(
                        HttpStatusCode.Forbidden,
                        "quotaExceeded")),
                    YoutubeCallPolicy.Refresh,
                    CancellationToken.None));
            var foregroundCalls = 0;

            var foreground = await Assert.ThrowsAsync<YoutubeQuotaExceededException>(() =>
                invoker.InvokeAsync(
                    _ => Task.FromResult(++foregroundCalls),
                    YoutubeCallPolicy.Foreground,
                    CancellationToken.None));

            Assert.Equal(new DateTimeOffset(2026, 8, 25, 7, 0, 0, TimeSpan.Zero), first.RetryAfter);
            Assert.Equal(first.RetryAfter, foreground.RetryAfter);
            Assert.Equal(0, foregroundCalls);
        }

        [Fact]
        public async Task InvokeAsync_RejectedAdmissionDoesNotInvokeOrRetryTransport()
        {
            using var invoker = CreateInvoker(
                new YoutubeSyncOptions { TransientRetryCount = 3 },
                TimeProvider.System,
                new RejectingLimiter());
            var calls = 0;

            await Assert.ThrowsAsync<YoutubeTransientException>(() => invoker.InvokeAsync(
                _ => Task.FromResult(++calls),
                YoutubeCallPolicy.Refresh,
                CancellationToken.None));

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ProductionLimiter_AllowsConfiguredConcurrencyButNoMore()
        {
            using var invoker = CreateInvoker(new YoutubeSyncOptions
            {
                MaximumConcurrentRequests = 2,
                RequestsPerSecond = 1_000,
                QueueCapacity = 10,
                TransientRetryCount = 0
            });
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var twoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var maximum = 0;

            var calls = Enumerable.Range(0, 5).Select(_ => invoker.InvokeAsync(async token =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                if (current == 2)
                {
                    twoEntered.TrySetResult();
                }
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return 1;
            }, YoutubeCallPolicy.Refresh, CancellationToken.None)).ToArray();

            await twoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref maximum));
            release.SetResult();
            await Task.WhenAll(calls);
            Assert.Equal(2, maximum);
        }

        [Fact]
        public async Task ProductionLimiter_IdleTimeDoesNotAccumulateBurstCredit()
        {
            using var invoker = CreateInvoker(new YoutubeSyncOptions
            {
                MaximumConcurrentRequests = 3,
                RequestsPerSecond = 10,
                QueueCapacity = 10,
                TransientRetryCount = 0
            });
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var stopwatch = Stopwatch.StartNew();
            var starts = new List<TimeSpan>();
            var sync = new object();

            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => invoker.InvokeAsync(token =>
            {
                lock (sync)
                {
                    starts.Add(stopwatch.Elapsed);
                }
                return Task.FromResult(1);
            }, YoutubeCallPolicy.Refresh, CancellationToken.None)));

            var ordered = starts.OrderBy(value => value).ToList();
            Assert.True(ordered[2] - ordered[0] >= TimeSpan.FromMilliseconds(75));
        }

        [Fact]
        public async Task RetryAfterObservation_IsIsolatedAcrossConcurrentAttempts()
        {
            var observer = new YoutubeHttpResponseObserver();

            var delays = await Task.WhenAll(Enumerable.Range(1, 8).Select(async seconds =>
            {
                using var observation = observer.BeginObservation();
                var response = new HttpResponseMessage();
                response.Headers.RetryAfter = new RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(seconds));
                observer.Capture(response);
                await Task.Yield();
                return observation.GetDelay(TimeProvider.System);
            }));

            Assert.Equal(
                Enumerable.Range(1, 8).Select(seconds => TimeSpan.FromSeconds(seconds)),
                delays.Cast<TimeSpan>());
        }

        [Fact]
        public async Task InvokeAsync_ConcurrentFailuresApplyTheirOwnRetryAfterObservations()
        {
            var time = new FakeTimeProvider();
            var limiter = new TrackingLimiter();
            var observer = new YoutubeHttpResponseObserver();
            using var invoker = new YoutubeCallInvoker(
                Options.Create(new YoutubeSyncOptions
                {
                    TransientRetryCount = 1,
                    InitialRetryDelay = TimeSpan.Zero,
                    MaximumRetryDelay = TimeSpan.Zero,
                    MaximumRetryJitterMilliseconds = 0
                }),
                NullLogger<YoutubeCallInvoker>.Instance,
                observer,
                time,
                limiter);
            var longCaptured = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var shortCaptured = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var longAttempts = 0;
            var shortAttempts = 0;
            var longCall = invoker.InvokeAsync(async _ =>
            {
                if (++longAttempts == 1)
                {
                    observer.Capture(ResponseWithRetryAfter(TimeSpan.FromSeconds(20)));
                    longCaptured.SetResult();
                    await shortCaptured.Task;
                    throw new HttpRequestException("expected long retry");
                }
                return "long";
            }, YoutubeCallPolicy.Refresh, CancellationToken.None);
            var shortCall = invoker.InvokeAsync(async _ =>
            {
                await longCaptured.Task;
                if (++shortAttempts == 1)
                {
                    observer.Capture(ResponseWithRetryAfter(TimeSpan.FromSeconds(10)));
                    shortCaptured.SetResult();
                    throw new HttpRequestException("expected short retry");
                }
                return "short";
            }, YoutubeCallPolicy.Refresh, CancellationToken.None);
            await shortCaptured.Task;
            await DrainContinuationsAsync();

            time.Advance(TimeSpan.FromSeconds(10));
            await DrainContinuationsAsync();
            Assert.Equal(1, longAttempts);
            Assert.Equal(1, shortAttempts);

            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "long", "short" }, await Task.WhenAll(longCall, shortCall));
            Assert.Equal(2, longAttempts);
            Assert.Equal(2, shortAttempts);
        }

        [Fact]
        public void RetryAfterObservation_RestoresNestedScopeAndNoHeaderDoesNotLeak()
        {
            var observer = new YoutubeHttpResponseObserver();
            using var outer = observer.BeginObservation();
            observer.Capture(ResponseWithRetryAfter(TimeSpan.FromSeconds(3)));
            using (var inner = observer.BeginObservation())
            {
                observer.Capture(ResponseWithRetryAfter(TimeSpan.FromSeconds(7)));
                Assert.Equal(TimeSpan.FromSeconds(7), inner.GetDelay(TimeProvider.System));
            }
            Assert.Equal(TimeSpan.FromSeconds(3), outer.GetDelay(TimeProvider.System));

            using var empty = observer.BeginObservation();
            observer.Capture(new HttpResponseMessage());
            Assert.Null(empty.GetDelay(TimeProvider.System));
        }

        [Fact]
        public void RetryAfterObservation_UsesInjectedTimeForAbsoluteDate()
        {
            var now = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
            var time = new FakeTimeProvider(now);
            var observer = new YoutubeHttpResponseObserver();
            using var observation = observer.BeginObservation();
            var response = new HttpResponseMessage();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(2));

            observer.Capture(response);

            Assert.Equal(TimeSpan.FromMinutes(2), observation.GetDelay(time));
        }

        private static YoutubeCallInvoker CreateInvoker(
            YoutubeSyncOptions options,
            TimeProvider timeProvider = null,
            RateLimiter limiter = null) =>
            new(
                Options.Create(options),
                NullLogger<YoutubeCallInvoker>.Instance,
                new YoutubeHttpResponseObserver(),
                timeProvider,
                limiter);

        private static HttpResponseMessage ResponseWithRetryAfter(TimeSpan delay)
        {
            var response = new HttpResponseMessage();
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            return response;
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            var observed = Volatile.Read(ref target);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref target, value, observed);
                if (previous == observed)
                {
                    return;
                }
                observed = previous;
            }
        }

        private static async Task DrainContinuationsAsync()
        {
            for (var index = 0; index < 6; index++)
            {
                await Task.Yield();
            }
        }

        private static GoogleApiException GoogleException(HttpStatusCode statusCode, string reason) =>
            new("youtube", "expected")
            {
                HttpStatusCode = statusCode,
                Error = new RequestError
                {
                    Errors = new[] { new SingleError { Reason = reason } }
                }
            };

        private sealed class TrackingLimiter : RateLimiter
        {
            private int _activeLeases;
            private int _acquisitions;

            public int ActiveLeases => Volatile.Read(ref _activeLeases);
            public int Acquisitions => Volatile.Read(ref _acquisitions);
            public override TimeSpan? IdleDuration => null;
            public override RateLimiterStatistics GetStatistics() => null;

            protected override RateLimitLease AttemptAcquireCore(int permitCount) => Acquire();

            protected override ValueTask<RateLimitLease> AcquireAsyncCore(
                int permitCount,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult<RateLimitLease>(Acquire());

            private RateLimitLease Acquire()
            {
                Interlocked.Increment(ref _acquisitions);
                Interlocked.Increment(ref _activeLeases);
                return new TestLease(true, () => Interlocked.Decrement(ref _activeLeases));
            }
        }

        private sealed class RejectingLimiter : RateLimiter
        {
            public override TimeSpan? IdleDuration => null;
            public override RateLimiterStatistics GetStatistics() => null;
            protected override RateLimitLease AttemptAcquireCore(int permitCount) =>
                new TestLease(false, null);
            protected override ValueTask<RateLimitLease> AcquireAsyncCore(
                int permitCount,
                CancellationToken cancellationToken) =>
                ValueTask.FromResult<RateLimitLease>(new TestLease(false, null));
        }

        private sealed class TestLease : RateLimitLease
        {
            private readonly Action _onDispose;

            public TestLease(bool isAcquired, Action onDispose)
            {
                IsAcquired = isAcquired;
                _onDispose = onDispose;
            }

            public override bool IsAcquired { get; }
            public override IEnumerable<string> MetadataNames => Array.Empty<string>();

            public override bool TryGetMetadata(string metadataName, out object metadata)
            {
                metadata = null;
                return false;
            }

            protected override void Dispose(bool disposing) => _onDispose?.Invoke();
        }
    }
}
