# Task 008c: Add Channel And Projection Contract Tests

Status: Not Started

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

Not completed.
