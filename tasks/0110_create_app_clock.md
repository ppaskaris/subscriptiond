# Task 001a: Create App Clock And Replace Ambient Time

Status: Completed

Depends On: 0100_document_target_architecture

## Goal

Introduce `IAppClock` and use it everywhere application code currently depends on ambient system time or randomized scheduling delays.

## Scope

- Add `IAppClock` with `UtcNow`, `UtcToday`, `RandomDelay`, and `UtcNowAfterRandomDelay`.
- Add a production implementation.
- Register `IAppClock` in dependency injection.
- Replace service-layer and repository-call-site uses of `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, and `Constants.RandomlyBetween` where they are part of app behavior.
- Move new/changed timestamp behavior to UTC.
- Update tests to use a fake clock where deterministic timestamp behavior is asserted.

## Out Of Scope

- Domain model refactor beyond what is needed to inject and pass clock values.
- Cosmos implementation.
- Worker rewrite.
- SQL schema changes.

## Validation

- Run unit tests.
- Run LocalDB tests if SQL-facing timestamp behavior changes.

## Implementation Summary

Added `IAppClock` and `AppClock`, registered the clock in dependency injection, and routed app-behavior timestamps and randomized delays through the clock. Services, hosted services, `ListController`, list view refresh timing, list max-age display, and the shared layout footer now use UTC clock values instead of ambient time. Added deterministic `FakeAppClock` coverage for list expiry/renewal, share-link lifecycle and consume timestamps, channel claim leases, and channel video refresh stale times.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 93 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 131 passed
