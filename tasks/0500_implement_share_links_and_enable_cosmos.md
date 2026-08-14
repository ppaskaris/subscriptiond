# Task 0500: Implement Share Links And Enable Cosmos

Status: Not Started

Depends On: 0400_implement_cosmos_list_and_channel_repositories

## Goal

Complete the simplified Cosmos provider, enable configuration selection, and prove the complete
anonymous application flow through the real application host.

## Scope

- Implement share-link create, list-by-list-ID, delete, delete-by-list-ID, and ETag-protected
  consumption in the `shareLinks` container.
- Preserve password collision retry behavior and never store a list token in a share document.
- Calculate TTL from the absolute expiry at every share-link write.
- Register the three Cosmos repositories, no-op Cosmos expiration purger, client, context,
  initializer/validator, and request telemetry behind `Persistence:Provider=Cosmos`.
- Remove the temporary failure path introduced by Task 0100.
- Keep SQL Server as the checked-in default provider.
- Validate required Cosmos credentials and names early without echoing their values.
- Add a full-host emulator test covering create list, authenticate/renew, discover/add channel,
  request-driven refresh, render videos, create/consume/delete share link, remove channel, and
  delete list.

## Out Of Scope

- SQL-to-Cosmos migration.
- Azure deployment.
- A worker-state, system, projection, or recovery repository.
- Multi-instance hosting.

## Validation

- Provider contracts pass for both SQL and Cosmos for every supported visible behavior.
- Genuine competing share-link consumes return the list token exactly once.
- Emulator tests verify physical list and share-link TTL deletion using task-isolated containers and
  bounded polling; there are no related references to repair.
- Full-host startup succeeds with Cosmos and fails clearly for missing credentials or container
  policy drift.
- Build, non-provider tests, LocalDB tests, full Cosmos emulator tests, and `git diff --check` pass
  sequentially.

## Implementation Summary

Not implemented.
