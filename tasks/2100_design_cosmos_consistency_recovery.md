# Task 021: Design Cosmos Consistency And Recovery

Status: Completed

Depends On: 1400_enable_cosmos_provider_and_validate_end_to_end

## Goal

Define a production-safe, RU-bounded recovery model for list membership and channel reverse references across Cosmos containers.

## Scope

- Document source-of-truth and convergence invariants for list membership, `subscribedListIds`, `subscriptionCount`, orphan markers, and TTL.
- Enumerate failure points for add, remove, explicit list deletion, automatic list TTL deletion, projection refresh, and application restart.
- Select a durable recovery mechanism that can discover both dead reverse references and missing reverse references without an unbounded account scan.
- Cover active and unavailable channels, including channels that will never enter stale refresh lookahead.
- Define retry, compensation, poison-item, observability, and bounded-work behavior.
- Define how recovery behaves under concurrent add/remove, list renewal/deletion, projection writes, and multiple application instances.
- Record the decision in the relevant `docs/` design documents and split implementation responsibilities between Tasks 2110 and 2120.

## Out Of Scope

- Implementing the selected mechanism.
- Changing the anonymous secret-link model.
- Production data migration.

## Validation

- The design includes a failure matrix showing the expected converged state after every durable side effect can fail.
- The design proves that no successful list membership can remain permanently missing from canonical channel state.
- The design proves that expired/deleted lists cannot keep unavailable channels alive indefinitely.
- The design includes RU bounds, document bounds, restart behavior, and multi-instance behavior.
- Existing Cosmos schema, implementation-contract, and worker-state documents are updated consistently.

## Implementation Summary

Designed a Cosmos-specific, source-of-truth recovery model in which list
`channels[]` owns membership, channel reverse references/count/orphan TTL are
derived, and render projections remain non-authoritative. Selected a dedicated
`recovery` container with one lifecycle document per list, deterministic
per-list/channel edge documents, and rotating cursor documents. Scalar
membership/projection version and pending fields make committed cross-container
work durably discoverable without unbounded operation arrays or list/channel
scans. Channel `subscriptionGeneration` and generation-bound list-id keysets
make projection traversal safe when reverse references mutate.

Specified add/remove, explicit delete, automatic TTL, renewal, canonical refresh,
projection, restart, and multi-instance behavior. The design includes a failure
matrix after every durable side effect, a convergence argument for missing and
dead references, ETag/current-list-truth concurrency semantics, provisional
channel capacity reservation, bounded leases, one conflict retry, continued
poison retries, structured observability, fixed document/item/RU bounds, durable
checkpoints, and operational timing/alert SLOs. Recovery is independent of
channel status/staleness, so unavailable channels participate. Lifecycle
`activeEdgeCount`/`edgeGeneration` changes transactionally with edge creation and
deleting retirement, caps total edge documents at 125, and requires zero-count plus
from-start verification before completion. Exact due-query keysets, cursor wrap
fairness, a durable cross-kind round-robin page-ticket cursor, composite indexes,
and bounded drift recount are specified. Membership traversal distinguishes and
atomically adopts its own expected retirement generation while restarting from
the beginning on external generation changes.

Updated the system design, implementation contracts, Cosmos schema and
implementation sketch, and worker state model. Task 2110 now owns the shared
recovery substrate, provider-neutral recovery port/scheduler, global cursors,
membership, and projection recovery; Task 2120 owns lifecycle deadlines,
renewal, explicit deletion, TTL observation, per-list cleanup checkpoints, and
final cleanup. Task 2600 now provisions five containers and the required
recovery indexes; Task 2610 covers recovery-container identity, readiness, and
health/drift semantics. A continuation-bounded bootstrap is explicitly required
before enabling the invariant over pre-existing Cosmos data, while production
migration itself remains out of scope.

Validation passed on 2026-07-25:

- Confirmed dependency Task 1400 is completed.
- Confirmed all ten changed files are Markdown/design/task files; no code or
  assembly version changed.
- Automated content checks found the required source-of-truth, failure matrix,
  convergence, unavailable-channel, poison, RU/document/work, restart, cursor,
  generation, transactional edge count/cap, exact query/index, worker-port, and
  multi-instance sections in all five required design documents and Tasks
  2110/2120/2600/2610. Checks also cover forced-RU cross-kind non-starvation and
  membership expected-versus-external edge-generation behavior.
- Automated local Markdown-link validation found no broken links in changed
  files.
- `git diff --check` passed.
