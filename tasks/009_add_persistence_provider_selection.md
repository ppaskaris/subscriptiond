# Task 009: Add Persistence Provider Selection

Status: Not Started

Depends On: 008a_add_provider_contract_test_harness, 008b_add_list_and_sharelink_contract_tests, 008c_add_channel_and_projection_contract_tests, 008d_add_worker_state_and_purger_contract_tests

## Goal

Allow persistence provider implementations to be selected by configuration while keeping SQL as the default.

## Scope

- Add persistence provider options.
- Move SQL registrations behind provider-specific registration methods.
- Keep `SqlServer` as default provider.
- Add placeholder/failing registration path for Cosmos if implementation is not complete.

## Out Of Scope

- Cosmos implementation.
- Data migration tooling.

## Validation

- Default configuration still boots SQL provider.
- Unit tests for options binding if helpful.
- Existing tests pass.

## Implementation Summary

Not completed.
