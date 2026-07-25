# Task 021a: Implement Recoverable Cosmos Membership

Status: Not Started

Depends On: 2100_design_cosmos_consistency_recovery

## Goal

Make list add/remove membership operations recover automatically after partial writes, conflicts, process termination, and restart.

## Scope

- Provision the Task 2100 `recovery` container, narrowed indexes, fixed-shape
  lifecycle/edge DTOs, recovery options, provider-neutral worker hook, scheduler
  field, ETag lease/claim support, bounded paging, RU accounting, backoff, and
  poison behavior. SQL implements the hook as no work.
- Implement generic startup-immediate recovery forcing, force-generation-safe
  scheduler completion, and durable per-work-kind global due-query cursors with
  fixed-cycle total-order keysets and fair wrap semantics.
- Add the durable cross-kind page-ticket cursor rotating Membership, Projection,
  EdgeDue, and LifecycleDue before each shared-budget page.
- Create the lifecycle record before a Cosmos list is created. Lifecycle
  renewal/deletion processing belongs to Task 2120.
- Implement the deterministic edge, provisional channel reservation, channel
  serialized-size preflight, list `membershipVersion`/pending flag, and
  current-list-truth repair protocol for add and remove.
- Implement the membership-specific per-list edge keyset bound to list
  membership version and lifecycle edge generation; this is distinct from Task
  2120's deleted-list cleanup checkpoint. Atomically adopt the exact generation
  returned by this worker's own candidate retirement and continue; restart from
  the beginning for any unexpected/external generation.
- Maintain lifecycle `activeEdgeCount`/`edgeGeneration` transactionally with
  active edge create/retire, coalesce deterministic pair retries, and enforce the
  125-active-edge hard bound.
- Preserve list document membership, canonical channel reverse references, `subscriptionCount`, orphan state, and TTL invariants.
- Ensure a failure after the list-side write cannot leave the canonical channel permanently unsubscribed or eligible for orphan deletion.
- Ensure retries and duplicate recovery work are idempotent.
- Bound recovery work per pass and preserve the one-retry optimistic-concurrency policy for each document write unless the design explicitly changes it.
- Emit structured logs and metrics for pending work, attempts, successful repairs, retries, poison work, and convergence latency.
- Add canonical-channel `projectionVersion`/pending markers and recover incomplete
  projection fan-out. Increment `subscriptionGeneration` on every normalized
  reverse-set change and use a list-id keyset bound to both generations.
  Projection dead-reference detection activates edge repair; it does not
  directly apply a stale add/remove decision.
- Keep SQL behavior correct behind the same provider-neutral application flow.

## Out Of Scope

- Automatic list-TTL deletion reconciliation, which is Task 2120.
- Explicit list-deletion edge seeding and lifecycle cleanup, which are Task 2120.
- Lifecycle deadline queries and per-list deleted-edge traversal/completion
  checkpoints, which are Task 2120. Generic global queue cursors remain in this
  task.
- Projection sizing.
- Production migration tooling.

## Validation

- Unit and emulator tests inject failure after every durable side effect in add and remove operations.
- Restart tests create partial state with one service provider/process, then prove a fresh provider converges it.
- Genuine concurrent emulator tests cover duplicate add, add/remove races, and recovery racing with user changes.
- Tests prove recovery is idempotent, bounded, observable, and preserves unrelated memberships.
- Emulator tests prove projection work survives failure/restart, preserves list
  recovery fields, and converges dead references for fresh and unavailable
  channels.
- A genuine concurrency test starts projection over `[A,B]`, persists progress
  after `A`, removes `A`, and proves the subscription-generation mismatch resets
  the keyset and still processes `B`.
- Tests enforce the 16-KiB recovery-document and 1.9-MiB channel-item ceilings,
  100-membership/125-total-edge-document bounds, transactional count/generation,
  fixed 25/100 item bounds, measured per-pass RU scheduling budget, lease
  takeover, poison retry, and no token/credential logging.
- Emulator query metrics/plans prove the exact membership, projection, edge, and
  lifecycle due-query shapes use their intended scalar/composite indexes.
  Cursor tests cover fixed `cycleNow`, tuple advancement, end-only wrap,
  behind-cursor insertion, restart, and multi-instance cursor conflicts.
- With an RU budget forced to exhaust after one page and Membership work
  continuously replenished, emulator tests prove the persisted ticket rotation
  offers Projection, EdgeDue (with a due poison item next in its keyset), and
  LifecycleDue within the next three admitted pages, including across process
  restart.
- Membership emulator tests prove this worker's candidate retirement atomically
  adopts its exact returned generation and continues the keyset, while a
  concurrent other-worker retirement produces an unexpected generation and
  forces a from-start restart without skipping an edge.
- SQL provider contract tests and opted-in Cosmos tests pass sequentially after the build.

## Implementation Summary

Not implemented.
