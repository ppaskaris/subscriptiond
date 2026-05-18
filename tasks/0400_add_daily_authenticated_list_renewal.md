# Task 005: Add Daily Authenticated List Renewal

Status: Completed

Depends On: 0110_create_app_clock, 0120_create_domain_models, 0300_refactor_list_read_models

## Goal

Renew list expiration at most once per UTC day on authenticated list access, and never from maintenance or projection reads.

## Scope

- Add `ExpirationRenewedOn` to domain.
- Add SQL schema migration and `Schema.sql` update.
- Add authenticated list access method that validates token and renews once per day.
- Update controllers to use authenticated access methods.
- Ensure maintenance/projection reads do not renew expiration.

## Out Of Scope

- Cosmos TTL implementation.
- Provider selection.

## Validation

- Unit tests with fake `IAppClock`.
- LocalDB integration tests for renewal behavior.

## Implementation Summary

Added daily authenticated list renewal to SQL-backed list access. The SQL schema and migration now include nullable `ExpirationRenewedOn`, Dapper maps SQL `DATE` values through a `DateOnly` type handler, and `ListService` validates route tokens with `TokenUtils` before renewing `ExpiredAfter` at most once per UTC day through a persistence-only repository update.

List video and channel projection reads now fetch only additional projection data for an already-retrieved list, so maintenance and projection paths do not renew list expiration or re-fetch list identity. List controllers now authenticate once through `GetAuthenticatedListAsync`, then pass that list into view-building methods.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"` (105 passed)
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build` (154 passed)
