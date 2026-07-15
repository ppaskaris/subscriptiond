# Task 009: Add Persistence Provider Selection

Status: Completed

Depends On: 0700_add_provider_contract_test_harness, 0710_add_list_and_sharelink_contract_tests, 0720_add_channel_and_projection_contract_tests, 0730_add_worker_state_and_purger_contract_tests

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

Added configuration-bound persistence options with `SqlServer` as the default provider and an explicit `Persistence.Provider` setting in the default application configuration.

Extracted SQL Server connection, repository, purger, projection, and Dapper type-handler registrations into `AddSqlServerPersistence`, with startup selecting the provider through `AddPersistence`. Selecting the not-yet-implemented `Cosmos` provider now fails during registration with an actionable configuration error.

Added focused tests for default SQL Server selection, explicit SQL Server selection, options binding, the complete SQL registration set, and the Cosmos placeholder failure path.

Validation passed:

- `dotnet build youtubed.sln`
- `dotnet test youtubed.sln --no-build --filter "Category!=LocalDb"`: 126 passed
- `YOUTUBED_RUN_LOCALDB_TESTS=true dotnet test youtubed.sln --no-build`: 202 passed
