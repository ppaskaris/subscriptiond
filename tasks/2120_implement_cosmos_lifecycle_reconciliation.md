# Task 021b: Implement Cosmos Lifecycle Reconciliation

Status: Not Started

Depends On: 2110_implement_recoverable_cosmos_membership

## Goal

Ensure explicit deletion and TTL expiration converge all channel reverse references and orphan lifecycle state, including unavailable channels.

## Scope

- Extend the Task 2110 recovery substrate with lifecycle record renewal,
  due-deadline queries and per-list deleted-edge keyset/checkpoint handling.
- For explicit deletion, mark lifecycle state deleting, seed/verify edges for
  every current list channel, and only then conditionally delete the list.
- For TTL deletion, treat the deadline only as a check time: point-read the list,
  reschedule from its current expiry when present, and page its recovery
  partition when it returns 404.
- Detect and repair dead list references for active, fresh, unavailable, and already-orphaned channels.
- Ensure channels with no valid list memberships receive the correct orphan marker and TTL.
- Ensure re-adding a channel during reconciliation clears orphan state and cannot be erased by stale repair work.
- Keep reconciliation incremental, keyset/checkpoint-aware, RU-bounded,
  restartable, and safe with multiple application instances.
- Traverse every active edge state in deterministic `(channelId,id)` order,
  adopt the expected generation from the worker's own transactional retirement,
  restart on an unexpected/external lifecycle `edgeGeneration` change, and
  update count/generation in that same-partition retirement batch.
- Complete a lifecycle only after a from-start active-edge query is empty,
  `activeEdgeCount` is zero, and the observed lifecycle ETag/generation still
  matches. Leased, poison, and newly created candidates must prevent completion.
- Add structured lifecycle/reconciliation metrics and actionable error logging.
- Update TTL and lifecycle design documentation with operational timing guarantees.

## Out Of Scope

- Changing list or channel retention durations without an explicit design decision.
- SQL-to-Cosmos migration.
- Production provisioning.
- Membership add/remove, projection-pending recovery, shared recovery container
  provisioning, and general lease/poison infrastructure completed by Task 2110.
- Generic startup-immediate scheduling and per-kind global queue cursors,
  completed by Task 2110.

## Validation

- Emulator tests prove explicit list deletion converges references after injected partial failures and restart.
- Emulator or Azure staging tests use short-lived test documents, wait with a bounded poll, and prove list TTL deletion leads to reverse-reference repair and eventual orphan-channel deletion.
- Coverage includes unavailable channels and races where membership is re-added during repair.
- Tests prove work is bounded and resumes correctly from keyset/checkpoint state.
- Tests inject failure after lifecycle deletion state, every edge-seeding write,
  list deletion, every channel repair, edge retirement, and lifecycle completion.
- Renewal-race tests prove an early lifecycle deadline cannot misclassify a
  renewed list as deleted; multi-instance tests prove stale cleanup cannot erase
  a concurrent re-add.
- Tests prove generation-bound keyset restart, transactional edge retirement,
  and full from-start completion verification under leased, poison, and newly
  inserted candidates. Repeated failed distinct adds must remain capped at 125
  total edge documents because transactional retirement deletes each edge.
- Injected counter drift must block completion, emit poison/health evidence, and
  converge through a generation-bound recount of only that bounded partition.
- Metrics/tests evidence lifecycle overdue age, 404 observations, orphan
  transitions, per-pass items/RU, poison retries, and lease takeover without
  exposing list tokens.
- Full sequential non-provider, LocalDB, and opted-in Cosmos suites pass.

## Implementation Summary

Not implemented.
