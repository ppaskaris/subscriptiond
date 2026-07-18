# Task 021b: Implement Cosmos Lifecycle Reconciliation

Status: Not Started

Depends On: 2110_implement_recoverable_cosmos_membership

## Goal

Ensure explicit deletion and TTL expiration converge all channel reverse references and orphan lifecycle state, including unavailable channels.

## Scope

- Implement the list-deletion and TTL-deletion recovery path selected by Task 2100.
- Detect and repair dead list references for active, fresh, unavailable, and already-orphaned channels.
- Ensure channels with no valid list memberships receive the correct orphan marker and TTL.
- Ensure re-adding a channel during reconciliation clears orphan state and cannot be erased by stale repair work.
- Keep reconciliation incremental, continuation-aware, RU-bounded, restartable, and safe with multiple application instances.
- Add structured lifecycle/reconciliation metrics and actionable error logging.
- Update TTL and lifecycle design documentation with operational timing guarantees.

## Out Of Scope

- Changing list or channel retention durations without an explicit design decision.
- SQL-to-Cosmos migration.
- Production provisioning.

## Validation

- Emulator tests prove explicit list deletion converges references after injected partial failures and restart.
- Emulator or Azure staging tests use short-lived test documents, wait with a bounded poll, and prove list TTL deletion leads to reverse-reference repair and eventual orphan-channel deletion.
- Coverage includes unavailable channels and races where membership is re-added during repair.
- Tests prove work is bounded and resumes correctly from continuation/checkpoint state.
- Full sequential non-provider, LocalDB, and opted-in Cosmos suites pass.

## Implementation Summary

Not implemented.
