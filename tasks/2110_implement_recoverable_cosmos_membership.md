# Task 021a: Implement Recoverable Cosmos Membership

Status: Completed

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

Implemented the Task 2100 recoverable Cosmos membership and projection design.
The provider now provisions the fifth `recovery` container with fixed-shape
lifecycle, deterministic edge, per-kind cursor, and cross-kind ticket documents.
Lifecycle/edge active-set changes use same-partition transactional batches,
ETag leases and one conflict retry, a 125-edge hard cap, 16-KiB document
preflight, expected-generation membership checkpoint adoption, bounded
backoff, durable poison retry, and sanitized structured logs/metrics.

Cosmos list creation now persists lifecycle evidence first. Add reserves
canonical channel capacity before committing membership; add/remove list writes
advance scalar membership versions and pending fields; recovery always rereads
current list truth before normalizing reverse references, count, orphan state,
TTL, and subscription generation. Channel writes enforce the 1.9-MiB ceiling.
Canonical refreshes persist projection versions/pending state, and projection
fan-out resumes from a list-id keyset bound to both projection and subscription
generations. Dead projection references activate due edge repair rather than
applying stale membership decisions.

Added the provider-neutral recovery port/budget/result, SQL no-work
implementation, SQL and Cosmos force-generation-safe scheduler fields, startup
forcing, worker scheduling, recovery options, DI, RU accounting, and durable
round-robin page admission across Membership, Projection, EdgeDue, and
LifecycleDue. Task 2120 lifecycle deadline/deletion handling remains out of
scope; this task provides and fairly observes its shared queue/cursor substrate.

Emulator validation revealed that Cosmos does not satisfy an `ORDER BY` with a
composite whose equality-filter fields are prepended. The schema now retains
the selective filter-leading composites and adds composites matching the actual
query-order tuples. The implementation contracts, schema plan, and
implementation sketch document that measured design correction.

Validation passed sequentially on 2026-07-25:

- `dotnet build youtubed.sln`: passed with 0 warnings and 0 errors.
- Tests excluding LocalDB and Cosmos: 182 passed, 0 failed, 0 skipped.
- Opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 79 passed,
  0 failed, 0 skipped.
- Opted-in Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 56
  passed, 0 failed, 0 skipped.
- Emulator coverage includes partial add/remove side effects and fresh-process
  recovery, genuine duplicate-add/add-remove concurrency, projection restart
  after a subscription-generation change, expected versus external edge
  generation changes, fixed-cycle and cross-kind cursor persistence,
  multi-instance ticket admission, lease takeover, durable membership/projection
  poison retry, legacy and interrupted lifecycle bootstrap, stale-retirement
  overlap, edge/document capacity ceilings, exact query index metrics, and
  measured per-item RU admission without advancing past unadmitted work.
- Final review fixes throw non-semantic retirement batch failures and give a
  semantic conflict only one exact-truth retry. Emulator coverage proves
  four-empty pass completion from every durable starting rotation, due
  Membership discovery after starting at each later kind, genuine same-kind
  cursor contention, unrelated reverse-membership preservation, error-severity
  poison logs, actual observed pending counts, membership/projection convergence
  latency, and ETag conflict/retry telemetry.
- `git diff --check`: passed.
