# Task 025a: Prove Cosmos TTL Lifecycle Behavior

Status: Not Started

Depends On: 2120_implement_cosmos_lifecycle_reconciliation

## Goal

Prove that TTL is an actual end-to-end cleanup mechanism for every Cosmos lifecycle, including related-reference convergence.

## Scope

- Add isolated short-TTL emulator tests for expired lists, expired and used share links, and orphan channels with embedded videos.
- Poll with bounded deadlines and useful diagnostics rather than using fixed long sleeps.
- Verify physical deletion, not merely the `ttl` property or no-op purger return value.
- After list TTL deletion, verify reverse references, `subscriptionCount`, orphan markers, and orphan TTL converge for active and unavailable channels.
- Verify renewal and re-subscription clear or recompute TTL without extending unrelated lifetimes.
- Document expected TTL and reconciliation latency ranges for operations and alerts.

## Out Of Scope

- Changing retention policy values unless testing exposes an explicit product need.
- Replacing Cosmos TTL with application bulk deletion.

## Validation

- Required TTL lifecycle tests pass against the local emulator before task completion.
- Failure output identifies the retained document, TTL value, timestamps, and pending reconciliation state.
- No test passes merely because `IExpirationPurger` returns zero.
- The opted-in local Cosmos suite runs the supported emulator tests without skips.

## Implementation Summary

Not implemented.
