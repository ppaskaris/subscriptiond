# Task 027: Make The Worker Safe For Multi-Instance Hosting

Status: Not Started

Depends On: 2500_add_adversarial_cosmos_concurrency_tests, 2610_harden_cosmos_identity_health_and_observability

## Goal

Prevent duplicate YouTube work and conflicting maintenance passes when more than one application instance is running, while recovering promptly after a worker crash.

## Scope

- Add a provider-neutral worker lease/claim contract or an equally explicit single-runner mechanism.
- Implement SQL and Cosmos coordination with owner identity, lease expiry, ETag/transaction protection, renewal, and crash recovery.
- Ensure only the lease owner starts a purge or YouTube batch and that losing a lease stops new external work safely.
- Preserve force-generation semantics so a forced refresh cannot be erased during lease turnover.
- Keep completed YouTube work eligible for persistence finalization when cancellation/lease loss occurs, as allowed by the worker design.
- Add lease acquisition, contention, renewal, loss, duration, and duplicate-suppression observability.
- Document supported scale-out behavior and deployment settings.

## Out Of Scope

- Increasing YouTube quota.
- Using an in-memory lock as production coordination.
- Production cutover.

## Validation

- Deterministic tests run two workers against the same SQL store and the same Cosmos store and prove only one begins each external-work batch.
- Tests cover lease expiry, owner crash, renewal failure, cancellation, forced refresh during turnover, and stale owner completion.
- Cosmos tests use genuine concurrent emulator operations; SQL tests use LocalDB transactions/concurrency.
- No completed force signal or successfully fetched refresh result is silently lost.
- Full mandatory CI passes with both provider suites unskipped.

## Implementation Summary

Not implemented.
