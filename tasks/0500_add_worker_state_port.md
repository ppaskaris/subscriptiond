# Task 006a: Add Worker State Port

Status: Completed

Depends On: 0110_create_app_clock, 0120_create_domain_models

## Goal

Introduce provider-neutral worker state before rewriting the background worker.

## Scope

- Add `IWorkerStateStore`.
- Add SQL `WorkerState` table to schema and migrations.
- Implement SQL worker state get-or-create.
- Implement force channel refresh.
- Implement complete channel pass with observed-state protection.
- Implement complete purge.

## Out Of Scope

- Expiration purger.
- Cosmos worker state implementation.
- Worker rewrite.

## Validation

- Unit tests for worker state semantics.
- LocalDB integration tests for SQL worker state persistence.

## Implementation Summary

Added provider-neutral `IWorkerStateStore` and a SQL-backed `WorkerStateRepository` registered in dependency injection. The SQL store now supports get-or-create initialization from `IAppClock`, forced channel refresh with `DateTimeOffset.MinValue`, observed-state-protected channel pass completion, nullable channel refresh scheduling, and purge completion. Added domain helper methods for worker-state due checks.

Added the `WorkerState` table to `Schema.sql` and a rerunnable SQL Server migration. LocalDB reset now clears worker state between tests. Updated the application assembly version to `2.9.0.0` for this shipped feature.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 109 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 164 passed
