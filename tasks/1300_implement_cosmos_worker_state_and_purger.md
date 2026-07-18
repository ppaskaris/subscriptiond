# Task 014: Implement Cosmos Worker State And Purger

Status: Completed

Depends On: 1000_implement_cosmos_documents_and_indexes

## Goal

Implement Cosmos worker state and no-op expiration purger.

## Scope

- Implement get-or-create scheduler document.
- Implement forced channel refresh.
- Implement protected channel refresh completion.
- Implement purge completion.
- Implement no-op `IExpirationPurger`.

## Out Of Scope

- Channel/list/share repositories.

## Validation

- Cosmos worker state contract tests pass.
- Tests prove forced refresh is not overwritten by stale completion.

## Implementation Summary

Added a Cosmos worker state store backed by the singleton `scheduler` document
in the system container. State creation uses a point read followed by create,
with conflict recovery for concurrent first use. Force, protected channel-pass
completion, and purge completion use ETag-guarded document replacements with
one retry after a concurrency conflict. Channel-pass completion compares both
the observed schedule and force generation before every write attempt so stale
completion cannot overwrite a newer or repeated forced refresh.

Added a Cosmos expiration purger that honors cancellation and returns zero for
all purge phases because Cosmos TTL owns physical cleanup. Added Cosmos worker
state and purger provider-contract suites and wired the system container into
the shared Cosmos contract fixture. Added a deterministic unit test that
injects an ETag precondition failure, returns a concurrently forced scheduler
document on re-read, and verifies stale completion does not issue another write.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 154 passed,
  25 Cosmos tests skipped because the opt-in environment variable was not set.
- `dotnet test youtubed.sln --no-build --filter "Category=Cosmos"` with
  `YOUTUBED_RUN_COSMOS_TESTS=true`: 25 passed, 0 skipped.
- `dotnet test youtubed.sln --no-build --filter "Category=LocalDb"` with
  `YOUTUBED_RUN_LOCALDB_TESTS=true`: 76 passed, 0 skipped.
- `git diff --check`
