# Task 0700: Implement Offline SQL-To-Cosmos Import

Status: Not Started

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

Not implemented.
