# Task 007b: Rebuild Unified Worker On SQL

Status: Not Started

Depends On: 006a_add_worker_state_port, 006b_add_expiration_purger_port, 007a_build_batched_channel_refresh_pipeline

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

Not completed.
