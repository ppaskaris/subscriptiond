# Task 0500: Implement Share Links And Enable Cosmos

Status: Completed

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

- Added the Cosmos share-link repository with create-only password writes, low-volume
  cross-partition list queries, list-scoped point deletes, delete-by-list behavior, and TTL
  recalculation on every create or consume write. Share documents contain only the list ID and
  lifecycle fields, never the list token. Consumption point-reads the list and returns its token
  only after the share document's ETag-protected used-state write succeeds; a competing conflict
  returns no token.
- Enabled `Persistence:Provider=Cosmos` registration for the list, channel, and share-link
  repositories, Cosmos TTL no-op purger, singleton client/context, container initializer and
  startup validator, and secret-safe request telemetry. Development startup creates and validates
  the documented three-container shape; non-development startup validates pre-provisioned shared
  throughput and container policy. The database name must be explicitly configured. Missing
  credentials or names fail during registration without echoing configured values, and missing
  production databases or containers are translated to safe errors without SDK exception chains,
  resource URIs, raw diagnostics, or configured resource names. SQL Server remains the checked-in
  default.
- Added Cosmos share-link provider contracts, genuine competing-consume coverage, serialized
  no-token and TTL-recalculation checks, bounded physical TTL deletion polling, and a full-host
  emulator flow covering list create/authentication/renewal, channel discovery/add/queued refresh,
  video rendering, share create/consume/delete, channel removal, and list deletion through the
  actual Cosmos registrations built by `Program`. Production-host tests cover safe startup failure
  for missing credentials, a missing database, and live container-policy drift. Updated the
  persistence boundary and provider-registration tests and incremented `AssemblyVersion` from
  `2.26.0.0` to `2.27.0.0` for the backward-compatible feature.
- Validation: `dotnet build youtubed.sln` passed with zero warnings and errors; tests excluding
  LocalDB and Cosmos passed 151/151 with no skips; opted-in LocalDB tests passed 50/50 with no
  skips; opted-in Cosmos emulator tests passed 25/25 with no skips; `git diff --check` and the
  equivalent trailing-whitespace scan across new files passed.
