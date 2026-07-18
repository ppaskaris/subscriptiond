# Task 021a: Implement Recoverable Cosmos Membership

Status: Not Started

Depends On: 2100_design_cosmos_consistency_recovery

## Goal

Make list add/remove membership operations recover automatically after partial writes, conflicts, process termination, and restart.

## Scope

- Implement the durable membership recovery mechanism selected by Task 2100.
- Preserve list document membership, canonical channel reverse references, `subscriptionCount`, orphan state, and TTL invariants.
- Ensure a failure after the list-side write cannot leave the canonical channel permanently unsubscribed or eligible for orphan deletion.
- Ensure retries and duplicate recovery work are idempotent.
- Bound recovery work per pass and preserve the one-retry optimistic-concurrency policy for each document write unless the design explicitly changes it.
- Emit structured logs and metrics for pending work, attempts, successful repairs, retries, poison work, and convergence latency.
- Keep SQL behavior correct behind the same provider-neutral application flow.

## Out Of Scope

- Automatic list-TTL deletion reconciliation, which is Task 2120.
- Projection sizing.
- Production migration tooling.

## Validation

- Unit and emulator tests inject failure after every durable side effect in add and remove operations.
- Restart tests create partial state with one service provider/process, then prove a fresh provider converges it.
- Genuine concurrent emulator tests cover duplicate add, add/remove races, and recovery racing with user changes.
- Tests prove recovery is idempotent, bounded, observable, and preserves unrelated memberships.
- SQL provider contract tests and opted-in Cosmos tests pass sequentially after the build.

## Implementation Summary

Not implemented.
