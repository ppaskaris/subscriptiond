# Task 0600: Validate Emulator And Azure Free Tier

Status: Not Started

Depends On: 0500_implement_share_links_and_enable_cosmos

## Goal

Establish a small evidence-based release envelope and prove the simplified provider against an
actual correctly provisioned Azure Cosmos DB free-tier database.

## Scope

- Define small, representative, and maximum supported list/channel datasets without padded
  synthetic metadata unrelated to the application's real limits.
- Measure serialized size, request count, RU, and latency for list rendering/renewal, add/remove,
  channel refresh, and share operations in the emulator.
- Retain a clear item-size safety margin and confirm the shared workload leaves the documented 30%
  throughput reserve under representative traffic.
- Provision or identify one manually approved free-tier test account and database following
  [`../docs/migration-and-operations.md`](../docs/migration-and-operations.md).
- Verify free-tier eligibility, 1,000 RU/s manual shared database throughput, one region, and exactly
  the three intended shared-throughput containers.
- Run a secret-safe Azure smoke test for every application flow and compare request shape and RU to
  emulator observations without requiring exact equality.
- Verify startup detects a deliberately wrong safe-to-test container policy in an isolated test
  database.
- Record the single-instance deployment constraint and ensure App Service scale-out is disabled.

## Out Of Scope

- Production data migration or cutover.
- Hosted CI, automatic deployment, dashboards, or an enterprise alert suite.
- Raising limits merely to make a measurement pass.

## Validation

- Emulator size/RU regression tests pass for all three representative shapes.
- The Azure smoke test passes against shared free-tier throughput without unhandled throttling.
- Azure portal/SDK evidence confirms the intended account, database, throughput, region, container,
  TTL, partition-key, and indexing configuration without exposing credentials.
- Logs contain no tokens, share passwords, keys, connection strings, bodies, or raw diagnostics.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, format verification,
  `git diff --check`, and the direct-plus-transitive vulnerability scan pass sequentially.

## Implementation Summary

Not implemented.
