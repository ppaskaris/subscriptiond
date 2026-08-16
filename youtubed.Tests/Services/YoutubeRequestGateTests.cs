using Google;
using Google.Apis.Http;
using Google.Apis.Requests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using youtubed.Services;

namespace youtubed.Tests.Services
{
    public sealed class YoutubeRequestGateTests
    {
        [Fact]
        public async Task ExecuteAsync_RetriesTransientNetworkFailureAndCountsActualAttempts()
        {
            using var gate = CreateGate(transientRetryCount: 1);
            var attempts = 0;

            var result = await gate.ExecuteAsync<int>(token =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<int>(new HttpRequestException("expected"))
                    : Task.FromResult(42);
            }, waitForCooldown: true, CancellationToken.None);

            Assert.Equal(42, result);
            Assert.Equal(2, attempts);
            Assert.Equal(2, gate.Snapshot.RequestAttempts);
            Assert.Equal(1, gate.Snapshot.Retries);
            Assert.Equal(1, gate.Snapshot.Cooldowns);
        }

        [Fact]
        public async Task ExecuteAsync_RetriesRateLimitReason()
        {
            using var gate = CreateGate(transientRetryCount: 1);
            var attempts = 0;

            var result = await gate.ExecuteAsync<int>(token =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<int>(GoogleException(
                        HttpStatusCode.Forbidden,
                        "rateLimitExceeded"))
                    : Task.FromResult(42);
            }, waitForCooldown: true, CancellationToken.None);

            Assert.Equal(42, result);
            Assert.Equal(2, gate.Snapshot.RequestAttempts);
            Assert.Equal(1, gate.Snapshot.Retries);
        }

        [Fact]
        public async Task ExecuteAsync_HonorsRetryAfterAndForegroundFailsFastDuringCooldown()
        {
            var responseObserver = new YoutubeHttpResponseObserver();
            using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            Assert.False(await responseObserver.HandleResponseAsync(
                new HandleUnsuccessfulResponseArgs { Response = response }));
            using var gate = CreateGate(transientRetryCount: 0, retryAfterProvider: responseObserver);
            await Assert.ThrowsAsync<YoutubeTransientException>(() => gate.ExecuteAsync<int>(
                token => Task.FromException<int>(new HttpRequestException("expected")),
                waitForCooldown: true,
                CancellationToken.None));

            await Assert.ThrowsAsync<YoutubeTransientException>(() => gate.ExecuteAsync(
                token => Task.FromResult(42),
                waitForCooldown: false,
                CancellationToken.None));
            Assert.Equal(1, gate.Snapshot.RequestAttempts);
        }

        [Fact]
        public async Task ExecuteAsync_QuotaExhaustionFailsForegroundFastWithoutAnotherAttempt()
        {
            using var gate = CreateGate(transientRetryCount: 2);
            await Assert.ThrowsAsync<YoutubeQuotaExceededException>(() => gate.ExecuteAsync<int>(
                token => Task.FromException<int>(GoogleException(
                    HttpStatusCode.Forbidden,
                    "quotaExceeded")),
                waitForCooldown: true,
                CancellationToken.None));

            await Assert.ThrowsAsync<YoutubeQuotaExceededException>(() => gate.ExecuteAsync(
                token => Task.FromResult(42),
                waitForCooldown: false,
                CancellationToken.None));
            Assert.Equal(1, gate.Snapshot.RequestAttempts);
            Assert.Equal(0, gate.Snapshot.Retries);
            Assert.Equal(1, gate.Snapshot.QuotaExhaustions);
        }

        [Fact]
        public async Task ExecuteAsync_BackgroundWaitDuringCooldownObservesCancellation()
        {
            using var gate = CreateGate(
                transientRetryCount: 0,
                transientCooldown: TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<YoutubeTransientException>(() => gate.ExecuteAsync<int>(
                token => Task.FromException<int>(new HttpRequestException("expected")),
                waitForCooldown: true,
                CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.ExecuteAsync(
                token => Task.FromResult(42),
                waitForCooldown: true,
                cancellation.Token));
            Assert.Equal(1, gate.Snapshot.RequestAttempts);
        }

        [Fact]
        public async Task ExecuteAsync_ClassifiesOtherGoogleErrorsAsPermanent()
        {
            using var gate = CreateGate(transientRetryCount: 2);

            await Assert.ThrowsAsync<YoutubePermanentException>(() => gate.ExecuteAsync<int>(
                token => Task.FromException<int>(GoogleException(
                    HttpStatusCode.BadRequest,
                    "invalidParameter")),
                waitForCooldown: true,
                CancellationToken.None));

            Assert.Equal(1, gate.Snapshot.RequestAttempts);
            Assert.Equal(0, gate.Snapshot.Retries);
        }

        [Fact]
        public async Task ExecuteAsync_AllowsOnlyOneInFlightRequest()
        {
            using var gate = CreateGate(transientRetryCount: 0);
            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = 0;
            var maximumInFlight = 0;

            var first = gate.ExecuteAsync(async token =>
            {
                maximumInFlight = Math.Max(maximumInFlight, Interlocked.Increment(ref inFlight));
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(token);
                Interlocked.Decrement(ref inFlight);
                return 1;
            }, waitForCooldown: true, CancellationToken.None);
            await firstEntered.Task;

            var second = gate.ExecuteAsync(token =>
            {
                maximumInFlight = Math.Max(maximumInFlight, Interlocked.Increment(ref inFlight));
                Interlocked.Decrement(ref inFlight);
                return Task.FromResult(2);
            }, waitForCooldown: true, CancellationToken.None);

            await Task.Yield();
            Assert.False(second.IsCompleted);
            releaseFirst.SetResult();
            Assert.Equal(new[] { 1, 2 }, await Task.WhenAll(first, second));
            Assert.Equal(1, maximumInFlight);
        }

        private static YoutubeRequestGate CreateGate(
            int transientRetryCount,
            TimeSpan? transientCooldown = null,
            IYoutubeRetryAfterProvider retryAfterProvider = null)
        {
            return new YoutubeRequestGate(
                Options.Create(new YoutubeSyncOptions
                {
                    RequestsPerSecond = 1_000_000,
                    TransientRetryCount = transientRetryCount,
                    InitialRetryDelay = TimeSpan.Zero,
                    MaximumRetryDelay = TimeSpan.Zero,
                    MaximumRetryJitterMilliseconds = 0,
                    TransientCooldown = transientCooldown.GetValueOrDefault()
                }),
                NullLogger<YoutubeRequestGate>.Instance,
                retryAfterProvider ?? new StubRetryAfterProvider());
        }

        private static GoogleApiException GoogleException(HttpStatusCode statusCode, string reason)
        {
            return new GoogleApiException("youtube", "expected")
            {
                HttpStatusCode = statusCode,
                Error = new RequestError
                {
                    Errors = new[] { new SingleError { Reason = reason } }
                }
            };
        }

        private sealed class StubRetryAfterProvider : IYoutubeRetryAfterProvider
        {
            public TimeSpan? ConsumeRetryAfter() => null;
        }
    }
}
