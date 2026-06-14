# Task 006b: Add Expiration Purger Port

Status: Completed

Depends On: 0110_create_app_clock, 0120_create_domain_models

## Goal

Move existing SQL cleanup behavior behind a provider-neutral expiration purger so SQL can delete expired data while Cosmos later no-ops in favor of TTL.

## Scope

- Add `IExpirationPurger`.
- Implement SQL expiration purger for expired lists, expired share links, and expired/orphan channel cleanup that exists at this stage.
- Move existing cleanup service/repository calls behind the purger without changing worker scheduling yet.

## Out Of Scope

- Worker state.
- Cosmos expiration purger.
- Unified worker rewrite.

## Validation

- Unit tests where useful.
- LocalDB integration tests for SQL purge behavior.

## Implementation Summary

Added provider-neutral `IExpirationPurger` with delete-count-returning purge methods for expired lists, expired share links, and expired/orphan channels. Implemented `SqlExpirationPurger` by reusing the existing SQL repository cleanup methods and `IAppClock`, then registered it in dependency injection.

Moved `MaintenanceHostedService` to call `IExpirationPurger` while preserving the existing maintenance schedule and delete-count logging. Expected shutdown cancellation now exits maintenance without logging purge errors. Removed maintenance-only cleanup methods from list, share-link, and channel service interfaces/implementations so cleanup is no longer exposed through user-facing services.

Added LocalDB integration coverage for SQL expiration purging through the new port. Updated `docs/implementation-contracts.md` to record that purge methods return delete counts, and bumped `youtubed/youtubed.csproj` `AssemblyVersion` for the shipped code change.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 109 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 164 passed
