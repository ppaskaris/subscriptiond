# Task 007b: Rebuild Unified Worker On SQL

Status: Completed

Depends On: 0500_add_worker_state_port, 0510_add_expiration_purger_port, 0600_build_batched_channel_refresh_pipeline

## Goal

Replace the two existing hosted services with one provider-agnostic worker while still using SQL as the backing provider.

## Scope

- Merge channel refresh and maintenance loops into one worker.
- Use worker state for `NextChannelRefreshAt` and `NextPurgeAt`.
- Use fixed purge interval.
- Call the batched channel refresh pipeline when channel work is due.
- Call `IExpirationPurger` when purge work is due.
- Use the in-process wake signal for forced channel refresh.
- Respect cancellation according to the worker state model.

## Out Of Scope

- Cosmos provider.
- Removing SQL `VisibleAfter`.
- Advanced YouTube quota limiter beyond bounded batches and fixed delay.

## Validation

- Unit tests for worker state transitions and cancellation flow.
- LocalDB integration tests for worker state interactions where practical.
- Manual review of logs for worker cadence.

## Implementation Summary

Replaced the registered background services with one `UnifiedWorkerHostedService` that reads provider-neutral worker state, runs SQL expiration purging on a fixed 10 minute interval, calls the batched channel refresh pipeline when channel work is due, completes worker state with observed-state protection, and sleeps until the next due time or an in-process wake signal.

Added `IWorkerWakeSignal`/`InProcessWorkerWakeSignal` and wired list channel additions to call `IWorkerStateStore.ForceChannelRefreshAsync` before pulsing the wake signal. The wake signal uses a version counter so a pulse cannot be missed between state read and worker sleep.

Extended channel refresh pipeline results with projection update counters for the unified worker pass summary log, and registered only `UnifiedWorkerHostedService` as the hosted worker in application startup. Bumped `AssemblyVersion` to `2.12.0.0`.

Follow-up review fixes preserved SQL worker coordination by adding a provider-neutral stale batch claim step before YouTube calls. SQL implements this by atomically advancing `VisibleAfter` for the selected batch, while the pipeline still uses lightweight lookahead for pass summary counters.

Channel scheduling now uses the provider-reported next effective active subscribed refresh time instead of clearing `NextChannelRefreshAt` whenever no work is currently due. SQL computes this as the later of `StaleAfter` and `VisibleAfter` for active subscribed channels. Purge execution now isolates list, share-link, and orphan-channel phases so one purge failure does not skip the remaining phases.

Added `ChannelRefreshForceCount` to worker state so completion cannot erase a forced refresh that happens during an already-forced pass. SQL increments the counter on every `ForceChannelRefreshAsync` call and `CompleteChannelRefreshPassAsync` now compares both the observed refresh time and observed force counter before updating `NextChannelRefreshAt`. Added a SQL migration and updated SQL/Cosmos design notes for the new force generation field. Bumped `AssemblyVersion` to `2.12.0.1` for the corrective follow-up.

Validation:

- `dotnet build youtubed.sln`: passed.
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: passed, 128 tests.
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: blocked because `MSSQLLocalDB` failed to start before tests reached project code (`SQL Server process failed to start`, LocalDB error `0x89c5010a`). A direct `sqllocaldb start MSSQLLocalDB` failed with the same startup error earlier in the task.
- Runtime cadence log review was not performed because the app was not run; the unified worker pass summary log was reviewed in code.
