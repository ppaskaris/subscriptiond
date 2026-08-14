# Task 0200: Simplify Refresh And Maintenance

Status: Completed

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

- Added one bounded, deduplicating in-memory channel-refresh queue and a queue-draining hosted
  service. The queue wakes blocked consumers, keeps queued and in-flight IDs within one capacity
  bound, drains explicit batches, and re-enqueues a batch after a failed refresh.
- Authenticated list rendering and channel-management reads now enqueue only missing or active stale
  channel IDs. The existing visible refresh flow now authenticates and enqueues every canonical ID
  in the target list, while adding a channel queues that newly added ID.
- Changed the refresh pipeline to accept and enforce an explicit bounded ID batch. It retains the
  existing cancellation-safe YouTube call boundaries and persists completed metadata/video results
  with a non-cancelable final save. Unit coverage includes cancellation between playlist pages,
  during a playlist request, between duration chunks, and mixed completed/incomplete batches;
  LocalDB coverage exercises metadata, video, and stale-time persistence end to end. Channel
  persistence is now the complete refresh write; the SQL projection writer and global stale/due
  queries were removed.
- Replaced the unified durable worker with the refresh hosted service and the existing simple
  periodic maintenance service. Removed worker state, force generations, recovery scheduling,
  consistency recovery, projection-writer ports, reverse subscription fields, their SQL
  implementations, and obsolete tests/contracts.
- Removed `WorkerState` from `Schema.sql` and added rerunnable
  `20260814_DropWorkerState.sql`, which drops both `dbo.WorkerState` and the schema-qualified
  `youtubed.WorkerState` shape used by an earlier intermediate migration. Incremented
  `AssemblyVersion` from `2.23.1.0` to `2.24.0.0` for this backward-compatible feature.
- Validation: `dotnet build youtubed.sln` passed with zero warnings and errors; tests excluding
  LocalDB and Cosmos passed 127/127 with no skips; opted-in LocalDB tests passed 50/50 with no
  skips, including the rerunnable drop migration; the production obsolete-type search returned no
  matches outside design/task history and earlier migrations; and `git diff --check` passed. The
  Cosmos suite was not applicable because this task deliberately leaves the Cosmos provider absent
  and changes no Cosmos implementation.
