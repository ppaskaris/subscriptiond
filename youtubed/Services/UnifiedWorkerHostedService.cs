using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Persistence;

namespace youtubed.Services
{
    public sealed class UnifiedWorkerHostedService : HostedService
    {
        private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

        private readonly IWorkerStateStore _workerStateStore;
        private readonly IExpirationPurger _expirationPurger;
        private readonly IChannelRefreshPipeline _channelRefreshPipeline;
        private readonly IConsistencyRecoveryService _consistencyRecovery;
        private readonly IWorkerWakeSignal _wakeSignal;
        private readonly IAppClock _clock;
        private readonly ILogger _logger;

        public UnifiedWorkerHostedService(
            IWorkerStateStore workerStateStore,
            IExpirationPurger expirationPurger,
            IChannelRefreshPipeline channelRefreshPipeline,
            IWorkerWakeSignal wakeSignal,
            IAppClock clock,
            ILogger<UnifiedWorkerHostedService> logger)
            : this(
                workerStateStore,
                expirationPurger,
                new SqlConsistencyRecoveryService(),
                channelRefreshPipeline,
                wakeSignal,
                clock,
                logger)
        {
        }

        public UnifiedWorkerHostedService(
            IWorkerStateStore workerStateStore,
            IExpirationPurger expirationPurger,
            IConsistencyRecoveryService consistencyRecovery,
            IChannelRefreshPipeline channelRefreshPipeline,
            IWorkerWakeSignal wakeSignal,
            IAppClock clock,
            ILogger<UnifiedWorkerHostedService> logger)
        {
            _workerStateStore = workerStateStore;
            _expirationPurger = expirationPurger;
            _consistencyRecovery = consistencyRecovery;
            _channelRefreshPipeline = channelRefreshPipeline;
            _wakeSignal = wakeSignal;
            _clock = clock;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await _workerStateStore.ForceConsistencyRecoveryAsync(cancellationToken);
            _wakeSignal.Pulse();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var observedWakeVersion = _wakeSignal.Version;
                    var state = await _workerStateStore.GetOrCreateAsync(cancellationToken);
                    var now = _clock.UtcNow;
                    if (state.IsPurgeDue(now)
                        || state.IsConsistencyRecoveryDue(now)
                        || state.IsChannelRefreshDue(now))
                    {
                        await RunPassAsync(state, cancellationToken);
                    }
                    else
                    {
                        await SleepUntilNextWorkAsync(
                            state,
                            observedWakeVersion,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception thrown while running unified worker.");
                    _logger.LogError(ex.ToString());
                    await Task.Delay(ErrorDelay, cancellationToken);
                }
            }
        }

        internal async Task<UnifiedWorkerPassResult> RunPassAsync(
            WorkerState state,
            CancellationToken cancellationToken)
        {
            var result = new UnifiedWorkerPassResult();
            var now = _clock.UtcNow;

            if (state.IsPurgeDue(now))
            {
                await RunPurgeAsync(result, cancellationToken);
            }
            else
            {
                result.NextPurgeAt = state.NextPurgeAt;
            }

            if (state.IsConsistencyRecoveryDue(now))
            {
                await RunConsistencyRecoveryAsync(result, state, cancellationToken);
            }
            else
            {
                result.NextConsistencyRecoveryAt = state.NextConsistencyRecoveryAt;
            }

            if (state.IsChannelRefreshDue(now))
            {
                await RunChannelRefreshAsync(
                    result,
                    state,
                    cancellationToken);
            }
            else
            {
                result.NextChannelRefreshAt = state.NextChannelRefreshAt;
            }

            LogPassSummary(result);
            return result;
        }

        private async Task RunConsistencyRecoveryAsync(
            UnifiedWorkerPassResult passResult,
            WorkerState observedState,
            CancellationToken cancellationToken)
        {
            ConsistencyRecoveryPassResult recoveryResult;
            try
            {
                recoveryResult = await _consistencyRecovery.RecoverAsync(
                    ConsistencyRecoveryPassBudget.Default,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Unified worker consistency recovery failed. Result={Result}.",
                    "Retry");
                recoveryResult = new ConsistencyRecoveryPassResult(
                    0,
                    0,
                    0,
                    1,
                    0,
                    0,
                    true,
                    _clock.UtcNow);
            }

            var next = recoveryResult.HasMoreEligibleWork
                ? _clock.UtcNow
                : Min(
                    recoveryResult.NextEligibleAt,
                    _clock.UtcNow.Add(Constants.ConsistencyRecoveryPollInterval));
            await _workerStateStore.CompleteConsistencyRecoveryPassAsync(
                observedState.NextConsistencyRecoveryAt,
                observedState.ConsistencyRecoveryForceCount,
                next,
                CancellationToken.None);
            passResult.ConsistencyRecovery = recoveryResult;
            passResult.NextConsistencyRecoveryAt = next;
        }

        internal async Task<bool> SleepUntilNextWorkAsync(
            WorkerState state,
            long observedWakeVersion,
            CancellationToken cancellationToken)
        {
            var delay = GetDelayUntilNextWork(state, _clock.UtcNow);
            if (delay <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                var woke = await _wakeSignal.WaitAsync(
                    observedWakeVersion,
                    delay,
                    cancellationToken);
                if (woke)
                {
                    _logger.LogInformation("Unified worker woke for forced channel refresh.");
                }

                return woke;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Unified worker sleep canceled.");
                throw;
            }
        }

        private async Task RunPurgeAsync(
            UnifiedWorkerPassResult result,
            CancellationToken cancellationToken)
        {
            result.PurgeRan = true;
            await RunPurgePhaseAsync(
                "expired lists",
                async () => result.ExpiredListDeleteCount =
                    await _expirationPurger.PurgeExpiredListsAsync(cancellationToken),
                cancellationToken);
            await RunPurgePhaseAsync(
                "expired share links",
                async () => result.ExpiredShareLinkDeleteCount =
                    await _expirationPurger.PurgeExpiredShareLinksAsync(cancellationToken),
                cancellationToken);
            await RunPurgePhaseAsync(
                "expired channels",
                async () => result.ExpiredChannelDeleteCount =
                    await _expirationPurger.PurgeExpiredChannelsAsync(cancellationToken),
                cancellationToken);

            var nextPurgeAt = _clock.UtcNow.Add(Constants.PurgeInterval);
            await _workerStateStore.CompletePurgeAsync(nextPurgeAt, CancellationToken.None);
            result.NextPurgeAt = nextPurgeAt;
        }

        private async Task RunPurgePhaseAsync(
            string phase,
            Func<Task> runAsync,
            CancellationToken cancellationToken)
        {
            try
            {
                await runAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception thrown while purging {0}.", phase);
                _logger.LogError(ex.ToString());
            }
        }

        private async Task RunChannelRefreshAsync(
            UnifiedWorkerPassResult passResult,
            WorkerState observedState,
            CancellationToken cancellationToken)
        {
            ChannelRefreshPipelineResult refreshResult = null;
            try
            {
                refreshResult = await _channelRefreshPipeline.RefreshStaleChannelsAsync(
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Unified worker channel refresh canceled before starting YouTube work.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception thrown while refreshing channels.");
                _logger.LogError(ex.ToString());
            }

            if (refreshResult != null)
            {
                passResult.ChannelRefresh = refreshResult;
                if (refreshResult.CanceledBeforeStartingYoutubeCall)
                {
                    _logger.LogInformation("Unified worker channel refresh canceled before starting YouTube work.");
                }
                else if (refreshResult.CanceledDuringYoutubeWork)
                {
                    _logger.LogInformation("Unified worker channel refresh canceled during YouTube work; finalized completed results.");
                }
            }

            var nextChannelRefreshAt = GetNextChannelRefreshAt(refreshResult, _clock.UtcNow);
            await _workerStateStore.CompleteChannelRefreshPassAsync(
                observedState.NextChannelRefreshAt,
                observedState.ChannelRefreshForceCount,
                nextChannelRefreshAt,
                CancellationToken.None);
            passResult.NextChannelRefreshAt = nextChannelRefreshAt;
        }

        private DateTimeOffset? GetNextChannelRefreshAt(
            ChannelRefreshPipelineResult refreshResult,
            DateTimeOffset now)
        {
            if (refreshResult == null ||
                refreshResult.CanceledBeforeStartingYoutubeCall ||
                refreshResult.CanceledDuringYoutubeWork)
            {
                return now;
            }

            if (refreshResult.StaleLookaheadCount == 0)
            {
                return refreshResult.NextChannelRefreshAt;
            }

            if (refreshResult.StaleLookaheadCount >= Constants.ChannelRefreshBatchSize ||
                refreshResult.StaleLookaheadCount > refreshResult.SelectedChannelCount)
            {
                return refreshResult.NextChannelRefreshAt.GetValueOrDefault(now);
            }

            return refreshResult.NextChannelRefreshAt;
        }

        private static TimeSpan GetDelayUntilNextWork(
            WorkerState state,
            DateTimeOffset now)
        {
            var next = state.NextPurgeAt;
            if (state.NextConsistencyRecoveryAt < next)
            {
                next = state.NextConsistencyRecoveryAt;
            }
            if (state.NextChannelRefreshAt.HasValue &&
                state.NextChannelRefreshAt.Value < next)
            {
                next = state.NextChannelRefreshAt.Value;
            }

            return next.Subtract(now);
        }

        private static DateTimeOffset Min(
            DateTimeOffset? left,
            DateTimeOffset right)
        {
            return left.HasValue && left.Value < right ? left.Value : right;
        }

        private void LogPassSummary(UnifiedWorkerPassResult result)
        {
            var channel = result.ChannelRefresh ?? new ChannelRefreshPipelineResult();
            _logger.LogInformation(
                "Unified worker pass completed. PurgeRan={PurgeRan}; ExpiredListsRemoved={ExpiredListsRemoved}; ExpiredShareLinksRemoved={ExpiredShareLinksRemoved}; ExpiredChannelsRemoved={ExpiredChannelsRemoved}; RecoveryExamined={RecoveryExamined}; RecoveryClaimed={RecoveryClaimed}; RecoverySucceeded={RecoverySucceeded}; RecoveryFailed={RecoveryFailed}; RecoveryPoison={RecoveryPoison}; RecoveryRequestCharge={RecoveryRequestCharge}; StaleChannelIdsDiscovered={StaleChannelIdsDiscovered}; SelectedChannels={SelectedChannels}; MetadataCalls={MetadataCalls}; PlaylistCalls={PlaylistCalls}; DurationCalls={DurationCalls}; ChannelsRefreshed={ChannelsRefreshed}; ChannelsMarkedUnavailable={ChannelsMarkedUnavailable}; ProjectionUpdatesAttempted={ProjectionUpdatesAttempted}; ProjectionUpdatesSucceeded={ProjectionUpdatesSucceeded}; NextChannelRefreshAt={NextChannelRefreshAt}; NextPurgeAt={NextPurgeAt}; NextConsistencyRecoveryAt={NextConsistencyRecoveryAt}.",
                result.PurgeRan,
                result.ExpiredListDeleteCount,
                result.ExpiredShareLinkDeleteCount,
                result.ExpiredChannelDeleteCount,
                result.ConsistencyRecovery?.Examined ?? 0,
                result.ConsistencyRecovery?.Claimed ?? 0,
                result.ConsistencyRecovery?.Succeeded ?? 0,
                result.ConsistencyRecovery?.Failed ?? 0,
                result.ConsistencyRecovery?.Poison ?? 0,
                result.ConsistencyRecovery?.RequestCharge ?? 0,
                channel.StaleLookaheadCount,
                channel.SelectedChannelCount,
                channel.MetadataCallCount,
                channel.PlaylistCallCount,
                channel.DurationCallCount,
                channel.RefreshedChannelCount,
                channel.UnavailableChannelCount,
                channel.ProjectionUpdateAttemptCount,
                channel.ProjectionUpdateSuccessCount,
                result.NextChannelRefreshAt,
                result.NextPurgeAt,
                result.NextConsistencyRecoveryAt);
        }
    }
}
