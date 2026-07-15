# Task 008d: Add Worker State And Purger Contract Tests

Status: Completed

Depends On: 0700_add_provider_contract_test_harness, 0500_add_worker_state_port, 0510_add_expiration_purger_port

## Goal

Add shared provider contract tests for worker state and expiration purging.

## Scope

- Add worker state get-or-create contract tests.
- Add forced channel refresh contract tests.
- Add protected channel pass completion contract tests.
- Add purge completion contract tests.
- Add expiration purger contract tests that assert domain-visible cleanup semantics.

## Out Of Scope

- List/share-link repository contracts.
- Channel repository contracts.
- Cosmos emulator fixture.

## Validation

- SQL contract tests pass under LocalDB opt-in.

## Implementation Summary

Added reusable worker-state provider contract tests covering get-or-create initialization and stability, forced refresh creation and generation increments, matching channel-pass completion including nullable scheduling, protection against both new and repeated forces during a pass, protection against a stale same-generation pass overwriting a newer schedule, and purge completion without changing channel-refresh state.

Added reusable expiration-purger provider contract tests with an explicit fixture capability for immediate deletion or no-op behavior. Immediate-deletion providers are checked for expired list deletion at the boundary with share-link and membership cascade effects, share-link retention-boundary cleanup, and orphan channel/video cleanup while subscribed channels remain visible. No-op providers are required to return zero and preserve all seeded domain-visible state. Expiration-sensitive records are seeded with a future cleanup time and made eligible by advancing only the fake application clock, preventing Cosmos emulator TTL cleanup from racing the no-op assertions. Cleanup assertions read through provider-neutral repository ports rather than inspecting SQL tables.

Bound both shared suites to the SQL provider through LocalDB opt-in test classes.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 123 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 199 passed
