# Task 008c: Add Channel And Projection Contract Tests

Status: Completed

Depends On: 0700_add_provider_contract_test_harness, 0200_add_channel_status, 0210_add_channel_url_lookup_cache

## Goal

Add shared provider contract tests for channel behavior and list projection updates.

## Scope

- Add canonical channel create/read/update contract tests.
- Add stale lookahead contract tests.
- Add unavailable channel exclusion contract tests.
- Add subscription reference/count contract tests where supported by the provider.
- Add projection update contract tests where SQL no-op behavior is acceptable.

## Out Of Scope

- List and share-link contracts.
- Worker state contracts.
- Cosmos emulator fixture.

## Validation

- SQL contract tests pass under LocalDB opt-in.

## Implementation Summary

Added shared provider contract coverage for canonical channel creation, batch reads, refresh updates, and video persistence. Added stale lookahead coverage for subscription eligibility, due-time and id ordering, result limits, and next-refresh discovery.

Added contracts proving unavailable channels are excluded from refresh work, list membership is reflected in canonical channel subscription references and counts, and projection updates propagate refreshed channel metadata, status, and videos without changing unrelated channels. SQL binds the shared suite through the existing LocalDB opt-in fixture; its dynamic joined projections satisfy the projection contract with the existing no-op projection writer.

Review follow-up strengthened the projection contract to pass the membership-aware canonical channel returned by the batch read, matching the Cosmos projection writer's use of `SubscribedListIds`. The contract now verifies every projected metadata/status field and video field for both the refreshed channel and a distinctive untouched channel.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 123 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 189 passed
