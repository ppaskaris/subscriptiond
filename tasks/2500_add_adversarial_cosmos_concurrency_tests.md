# Task 025: Add Adversarial Cosmos Concurrency And Recovery Tests

Status: Completed

Depends On: 2120_implement_cosmos_lifecycle_reconciliation, 2200_make_authenticated_cosmos_list_render_single_read

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
- SQL-to-Cosmos migration rehearsal.
- Weakening the one-retry document conflict policy.

## Validation

- Each race runs repeatedly without producing multiple share-link consumers, lost membership, stale projection overwrite, or erased worker force signals.
- Partial-state tests converge after retry/restart with no manual user retry.
- Tests assert final persisted documents and domain-visible behavior, not only successful return values.
- All new tests run without skips in the opted-in local Cosmos suite.
- Full sequential LocalDB and Cosmos provider contract suites remain green.

## Implementation Summary

Added repeated, genuine Cosmos emulator races for two-instance share-link
consumption, concurrent canonical-channel discovery, simultaneous membership
add/remove and projection refresh, explicit list deletion overlapping list
mutation, channel/recovery worker force-generation versus completion, and
multi-instance consistency recovery. Each case asserts final Cosmos documents
and repository-visible state, including exactly one returned share token,
normalized reverse membership/counts, current projections, reconciled deletion
references, and preserved scheduler force signals.

Internal interleaving callbacks now identify every confirmed durable creation,
membership, projection, bootstrap, and deletion/reconciliation boundary. The
emulator matrices inject failure after two list-creation side effects, seven
add side effects, six remove side effects, five normal projection side effects
(including conditional checkpoint reset), three dead-reference projection
side effects, and all fourteen lifecycle/bootstrap/cleanup side effects. Each
case verifies the exact injected exception, disposes the interrupted application
service provider, creates fresh providers for recovery, and asserts authoritative
list truth, canonical reverse references/counts, recovery edges, pending flags,
projected content, and lifecycle completion.

Repository write results now distinguish confirmed writes from missing,
version-changed, or no-op outcomes before firing a durable-boundary callback.
Projection, pending-clear, checkpoint-reset, and analogous legacy update paths
retry one optimistic-concurrency conflict, then throw the repository's semantic
conflict exception if the second attempt conflicts. Lifecycle creation reports
whether it actually created a document, so bootstrap and dead-reference hooks
cannot report a pre-existing lifecycle as a new durable side effect.

The multi-instance case interrupts add after its list commit, creates separate
pending projection work, and starts two fresh application providers together.
Deterministic barriers prove one is inside Membership while the other is inside
Projection; the test inspects both persisted partial states before releasing
them, requires both instances to claim work without failures, and asserts final
convergence immediately without sequential cleanup. Mutation races likewise
barrier all participants and retain every exception: only the documented
one-retry conflict/lease outcomes are accepted. Race identifiers include the
repetition number and unique document ids for actionable failure output.

Validation passed sequentially on 2026-08-03:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors.
- Tests excluding LocalDB and Cosmos: 196 passed, 0 failed, 0 skipped.
- Opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 71 passed,
  0 failed, 0 skipped.
- Opted-in Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 75
  passed, 0 failed, 0 skipped. All adversarial and durable-boundary cases ran
  without skips; individual races repeat between six and twelve times.
- `git diff --check`: passed.

The application `AssemblyVersion` is incremented from `2.22.0.0` to `2.22.1.0`
for the backward-compatible optimistic-concurrency and recovery reliability
corrections.
