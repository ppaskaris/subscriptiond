# Task 0700: Implement Offline SQL-To-Cosmos Import

Status: Completed

Depends On: 0600_validate_emulator_and_azure_free_tier

## Goal

Implement the narrow, idempotent offline import defined in
[`../docs/migration-and-operations.md`](../docs/migration-and-operations.md) without building a
general migration platform.

## Scope

- Add `validate`, `import`, and `reconcile` modes with explicit SQL source and Cosmos target
  configuration.
- Require a confirmed empty target for the first import and reject a target that has accepted
  post-cutover writes.
- Read SQL in bounded batches and map non-expired lists, their sorted distinct memberships,
  referenced channels, and the newest 100 channel videos into final document shapes.
- Preserve list IDs, token bytes, settings, absolute expiry, renewal dates, channel status, and
  stale timestamps.
- Recompute list TTL from the original absolute expiry and skip data already expired at import time.
- Do not migrate share links or worker state.
- Use deterministic IDs and upserts so an interrupted pre-cutover import can be rerun safely.
- Reuse the production serializer, mapper, limits, and Cosmos retry policy.
- Emit counts and opaque reconciliation hashes/identifiers without tokens, passwords, connection
  strings, document bodies, or personal metadata.

## Out Of Scope

- Durable checkpoints, leases, poison queues, or migration documents in application containers.
- Dual writes or online delta capture.
- Cosmos-to-SQL rollback import.
- Changing the production provider configuration.

## Validation

- Unit tests cover every SQL-to-document field mapping, ordering, expiry boundaries, token
  preservation without output, maximum cardinality, cancellation, and rerun behavior.
- `validate` and `reconcile` make no target mutations.
- LocalDB-to-emulator tests import a complete representative dataset, interrupt the first import,
  rerun it, and compare domain-visible behavior through the provider interfaces.
- Tests prove expired lists, unreferenced channels, share links, and worker state are not imported.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, and `git diff --check` pass
  sequentially.

## Implementation Summary

- Added the `import-sql-to-cosmos` offline command with explicit `validate`, `import`, and
  `reconcile` modes plus required SQL connection, Cosmos connection, and Cosmos database
  arguments. The command validates the production three-container/shared-throughput target shape,
  uses the production Cosmos client serializer and SDK retry configuration, accepts bounded batch
  sizes from 1 through 100, converts Ctrl+C into cooperative cancellation, and reports only counts
  and an opaque aggregate reconciliation hash. Controlled operator errors provide fixed actionable
  guidance, while raw provider failures omit exception details so connection strings, raw SDK
  diagnostics, document bodies, tokens, share passwords, and personal metadata are not exposed.
- Added bounded keyset SQL reads for non-expired lists and only their referenced channels. Lists
  preserve IDs, copied token bytes, settings, absolute expiry, renewal date, and sorted distinct
  membership; channels preserve metadata, status/reason/update time, stale time, and the newest 100
  videos. Mapping and serialization reuse the production Cosmos mapper, limits, document shapes,
  and serializer. Share links, expired lists, expired-only and unreferenced channels, and worker
  state are never read into target shapes. List TTL is recomputed from the original absolute expiry
  at each list write using the application clock, so delayed and repeated imports cannot extend the
  original deadline.
- The first import requires `--confirm-empty-target` and independently verifies that all three
  target containers are empty. A non-empty retry requires `--confirm-pre-cutover-rerun`, rejects
  every share link, unexpected deterministic ID, or semantic field mutation, and accepts only a
  subset matching an interrupted import. Channels and lists use deterministic point-partitioned
  upserts, so a stopped-site pre-cutover import can safely restart without checkpoints or migration
  documents. Reconciliation performs no writes and compares all imported semantic fields plus
  domain mapping. Validation serializes every source shape without enumerating target items or
  mutating target data; command startup still performs the required read-only target resource-shape
  validation.
- Added unit coverage for every list/channel/video field, copied token bytes without output,
  deterministic membership/video ordering, expiry-derived TTL, exact 100-item cardinality limits,
  read-only modes, Ctrl+C and mid-import cancellation, cancellation-safe restart, delayed/rerun TTL
  recomputation, actionable controlled errors, raw-provider redaction, interrupted rerun, and
  rejection of target mutations/share links.
  Added an opted-in LocalDB paging/mapping test for the exact expiry boundary and exclusions. Added
  a LocalDB-to-emulator test that interrupts after each of the two channel writes and the list write,
  restarts and reconciles after every durable side effect, then verifies list authentication data,
  settings, membership, channel status/staleness, newest-100 videos, TTL, size safety, and exclusions
  through production provider interfaces and direct secret-safe container checks.
- Final validation passed sequentially on 2026-08-14: build with zero warnings/errors, 164
  non-LocalDB/non-Cosmos tests, 51 opted-in LocalDB tests, and 28 opted-in Cosmos emulator tests.
  `git diff --check` passed for tracked changes and equivalent no-index whitespace checks passed for
  all new files. `AssemblyVersion` was incremented from `2.28.0.1` to `2.29.0.0`.
