# Task 025: Add Adversarial Cosmos Concurrency And Recovery Tests

Status: Not Started

Depends On: 2120_implement_cosmos_lifecycle_reconciliation, 2200_make_authenticated_cosmos_list_render_single_read, 2400_add_mandatory_provider_ci_release_gate

## Goal

Prove required concurrency and partial-failure guarantees against real Cosmos behavior rather than relying primarily on scripted SDK mocks.

## Scope

- Add genuine concurrent emulator tests for:
  - two consumers racing for one share link, with exactly one token returned;
  - concurrent discovery of the same channel;
  - simultaneous membership add/remove and projection refresh;
  - list mutation racing with explicit deletion and reference reconciliation;
  - worker force/completion generation races;
  - recovery running concurrently on multiple application instances.
- Add deterministic fault injection at repository workflow boundaries to simulate failure after each durable side effect.
- Recreate service providers between failure and recovery to prove restart safety.
- Retain focused unit tests where they provide faster diagnosis, but do not use mocks as the only evidence for Cosmos concurrency contracts.
- Capture enough diagnostics to reproduce failed interleavings.

## Out Of Scope

- General load testing.
- Production migration rehearsal.
- Weakening the one-retry document conflict policy.

## Validation

- Each race runs repeatedly without producing multiple share-link consumers, lost membership, stale projection overwrite, or erased worker force signals.
- Partial-state tests converge after retry/restart with no manual user retry.
- Tests assert final persisted documents and domain-visible behavior, not only successful return values.
- All new tests run without skips in the mandatory CI provider gate.
- Full sequential LocalDB and Cosmos provider contract suites remain green.

## Implementation Summary

Not implemented.
