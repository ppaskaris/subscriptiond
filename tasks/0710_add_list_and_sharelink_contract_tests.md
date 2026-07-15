# Task 008b: Add List And ShareLink Contract Tests

Status: Completed

Depends On: 0700_add_provider_contract_test_harness

## Goal

Add shared provider contract tests for list and share-link behavior.

## Scope

- Add list create/read/update/delete contract tests.
- Add authenticated list access and once-per-day renewal contract tests.
- Add list channel membership contract tests.
- Add list channel and list video read-model contract tests.
- Add share-link create/list/consume/delete contract tests.

## Out Of Scope

- Channel stale lookahead and refresh contracts.
- Worker state contracts.
- Cosmos emulator fixture.

## Validation

- SQL contract tests pass under LocalDB opt-in.

## Implementation Summary

Added reusable list provider contract tests covering create/read/update/delete, authenticated token access and once-per-UTC-day expiration renewal at both the service and direct provider boundary, idempotent channel membership updates, and channel/video projection read models including ordering and a global cross-channel video limit.

Added reusable share-link provider contract tests covering globally duplicate-resistant creation across lists, newest-first listing, single-use and expiration-aware consumption, targeted deletion, and deletion by list. Added SQL-backed LocalDB wrappers for both shared suites while preserving the existing opt-in behavior.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 123 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 184 passed
