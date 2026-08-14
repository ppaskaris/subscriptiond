# Task 0200: Simplify Refresh And Maintenance

Status: Not Started

Depends On: 0100_retire_existing_cosmos_provider

## Goal

Replace durable scheduling and projection-oriented refresh with a request-driven, best-effort
single-instance worker while proving the behavior first on SQL Server.

## Scope

- Add one bounded in-memory channel-refresh queue that de-duplicates canonical channel IDs and wakes
  the worker when new work arrives.
- Queue active stale channels after authenticated list or channel-management reads.
- Make the existing force-refresh flow enqueue every channel in the target list.
- Change the refresh pipeline to process an explicit bounded ID batch instead of discovering global
  stale work through provider-specific due queries.
- Retain cancellation behavior: stop starting new YouTube calls after cancellation and persist
  already completed results.
- Replace `UnifiedWorkerHostedService` with a small queue-draining refresh service and a simple
  periodic SQL maintenance service for expired lists, share links, and orphan channels.
- Remove `IWorkerStateStore`, `WorkerState`, durable force generations, due timestamps, consistency
  recovery scheduling, and their implementations/tests.
- Remove `IConsistencyRecoveryService` and `IListProjectionRepository`; channel writes become the
  complete refresh persistence operation.
- Remove `WorkerState` from `Schema.sql`. Add a rerunnable SQL migration to drop the table if this
  branch's intermediate schema may already have been applied; inspect all earlier worker-state
  migrations before deciding the exact migration sequence.
- Simplify repository interfaces by deleting methods used only for global stale discovery,
  projection fan-out, recovery, or durable scheduling.
- Preserve visible list rendering, status, renewal, add/remove, sharing, and existing routes.

## Out Of Scope

- A durable queue.
- Multi-instance worker coordination.
- Cosmos implementation.
- Reintroducing SQL `VisibleAfter` claims.

## Validation

- Queue unit tests prove de-duplication, capacity bounds, wake-up, drain behavior, cancellation, and
  re-enqueue after a failed refresh.
- Service/controller tests prove list access queues only missing or active stale channels and force
  refresh queues every list channel.
- Refresh-pipeline tests prove bounded batches and completed-result finalization.
- LocalDB tests prove SQL read/write behavior and the rerunnable schema migration.
- Build, non-provider tests, LocalDB tests, and `git diff --check` pass sequentially.

## Implementation Summary

Not implemented.
