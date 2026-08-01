# Cosmos Implementation Sketch

This document sketches how the Cosmos DB provider should implement the provider-neutral interfaces after SQL has been refactored to the new architecture.

## Provider Registration

Persistence should be selected by configuration:

```json
{
  "Persistence": {
    "Provider": "SqlServer"
  }
}
```

or:

```json
{
  "Persistence": {
    "Provider": "Cosmos"
  },
  "Cosmos": {
    "Endpoint": "...",
    "DatabaseName": "youtubed",
    "ListsContainer": "lists",
    "ChannelsContainer": "channels",
    "ShareLinksContainer": "shareLinks",
    "SystemContainer": "system",
    "RecoveryContainer": "recovery"
  }
}
```

The SQL provider remains default until Cosmos contract tests pass.

The implemented provider also accepts `Cosmos:ConnectionString` as an alternative
to `Cosmos:Endpoint` plus `Cosmos:Key`, which is useful for emulator and secret-based
configuration. On application startup it creates the configured database and
containers when absent. Cosmos SDK request charges are logged at debug level.

## Document DTOs

Cosmos DTOs should stay in `Persistence/Cosmos` and map to domain objects.

Suggested DTOs:

- `CosmosListDocument`
- `CosmosProjectedChannelDocument`
- `CosmosVideoDocument`
- `CosmosChannelDocument`
- `CosmosShareLinkDocument`
- `CosmosWorkerStateDocument`
- `CosmosListLifecycleDocument`
- `CosmosMembershipEdgeDocument`

Do not expose Cosmos DTOs from repository interfaces.

## List Repository

Authenticated list access:

1. for the normal list page, enter one request-count/request-charge scope and
   point-read the list document
2. compare route token with stored raw token using constant-time comparison
3. map the requested bounded projection from that same document
4. if authenticated and `expirationRenewedOn != clock.UtcToday`, conditionally
   replace with the already-read ETag after updating:
   - `expiredAfter`
   - `expirationRenewedOn`
   - `ttl`
5. on a 412, point-read and reapply once; on a concurrent-delete 404 return no
   projection
6. return the requested domain read model

The common same-day list page is exactly one list point read. Renewal is one
initial read plus one conditional list write; it does not synchronously update
the lifecycle record because the prior lifecycle deadline safely causes an
early authoritative check and reschedule. Record operation histograms for SDK
request count and RU tagged by outcome. A representative one-channel/one-video
document is budgeted at at most 10 RU same-day and at most 25 RU including the
renewal write; the maximum supported list-document point read remains bounded
separately at 350 RU.

Read-model reads:

- point-read list document
- map embedded channels to `ListChannelProjection` for channel management
- map embedded channels with nested videos to hierarchical `ListVideoProjection` for the main list page
- do not renew expiration

Add channel:

1. Create/claim the deterministic recovery edge for `(listId, channelId)`.
   New active edges and lifecycle `activeEdgeCount`/`edgeGeneration` are one
   list-partition transactional batch; reject the 126th active edge.
2. Point-read and conditionally update the channel with a provisional reverse
   reference, cleared orphan state, and disabled TTL. Use the configured
   serializer to reject an oversized channel before list membership can commit.
3. Point-read the list with ETag, append the bounded projection, increment
   `membershipVersion`, set `membershipRecoveryPending`, initialize
   `membershipRecoveryStartedAt`, and reset its attempt/poison/error fields.
4. Mark the edge tracked and reconcile it from a fresh list read.
5. Clear the pending flag only if the reconciled membership version is unchanged.

The request has a bounded edge-owner lease and may not perform step 3 after that
lease expires. A conflict at either item gets one reread/retry.

Remove channel:

1. Ensure/activate the deterministic recovery edge.
2. Point-read the list with ETag, remove the embedded projection, increment
   `membershipVersion`, set `membershipRecoveryPending`, initialize
   `membershipRecoveryStartedAt`, and reset its attempt/poison/error fields.
3. Reconcile the edge from a fresh list read, conditionally removing only this
   list id from the channel and applying orphan TTL if it was the last.
4. Retire the edge and clear only the repaired list version.

Repeated removes perform repair even if membership is already absent.

Create writes the lifecycle record before creating the list. Authenticated
renewal advances it after renewing the list; a missed lifecycle update causes
only an early point-read. Explicit delete marks the lifecycle record deleting
and ensures all current list channels have edge records before deleting the list.

## Channel Repository

Channel id is canonical. Submitted URL lookup uses bounded in-memory cache and YouTube resolution, not durable URL uniqueness.

Stale lookahead:

- cross-partition query for lightweight records
- filter active subscribed channels:
  - `staleAfter <= now`
  - `subscriptionCount > 0`
  - `status == Active`
- return up to `ChannelRefreshLookaheadCount`

Batch reads:

- point-read full channel docs by id
- recheck stale/subscription/status in memory before processing

Save refreshed channels:

- replace canonical channel documents with ETag
- update metadata, playlist id, videos, status, failure fields, stale timestamp

Reverse reference repair:

- activate an edge record rather than trusting the stale command outcome
- point-read the list and derive presence from its current `channels[]`
- add/remove only that list id, preserve unrelated ids, sort/deduplicate, and set
  `subscriptionCount = subscribedListIds.Count`
- clear orphan state and TTL for a positive count
- if count is zero, retain/set `orphanedAfter` and set orphan TTL
- increment `subscriptionGeneration` whenever the normalized reverse-reference
  set changes
- retry a conflicted channel write once; leave the edge due if both attempts fail

Recovery does not use stale lookahead. It point-reads active, fresh, unavailable,
and already-orphaned channels by edge id.

## List Projection Repository

SQL implementation is no-op.

Cosmos implementation:

1. The canonical refresh write increments `projectionVersion` and sets
   `projectionRecoveryPending`, initializes `projectionRecoveryStartedAt`, and
   resets its attempt/poison/error fields.
2. For each current subscribed list id, point-read the list.
3. If missing or the list no longer contains the channel, activate its edge
   record for authoritative membership repair.
4. If the list contains the channel, replace that channel subdocument in memory.
5. Write the list document with ETag, preserving membership recovery fields.
6. On conflict, re-read and reapply once.
7. Clear projection pending only if the canonical version is unchanged.

Projection updates should only touch channels processed in the current batch. Do not point-read every other channel in the list just to rebuild a perfect projection.

Projection progress is a sorted-list-id keyset bound to both
`projectionVersion` and `subscriptionGeneration`. Either generation changing
clears the key and restarts; completion conditionally checks both. An indexed
bounded worker query also finds pending projection versions left by a crash, so
a fresh canonical write cannot become permanently stranded merely because the
channel is now fresh or unavailable.

## Share Link Repository

Create:

- generate password
- create document with password as id
- set TTL to retention cleanup time
- retry on id conflict

List by list:

- query by `listId`
- order by `createdAt DESC`, then password

Consume:

1. point-read by password
2. reject if expired or used
3. point-read target list
4. reject if missing
5. patch `usedAt` with ETag
6. return list id and token

Delete:

- missing share link deletion can be treated as success when list auth already succeeded

## Worker State Store

Use the `system` container.

`ForceChannelRefreshAsync`:

- set `nextChannelRefreshAt` to `DateTimeOffset.MinValue`
- increment `channelRefreshForceCount`

`CompleteChannelRefreshPassAsync`:

- conditionally update `nextChannelRefreshAt` only if the stored value and `channelRefreshForceCount` still match the worker's observed values
- if the condition fails, leave the forced or newer state intact

`CompletePurgeAsync`:

- set `nextPurgeAt`

`ForceConsistencyRecoveryAsync`:

- set `nextConsistencyRecoveryAt` to `DateTimeOffset.MinValue`
- increment `consistencyRecoveryForceCount`

`CompleteConsistencyRecoveryPassAsync`:

- conditionally update `nextConsistencyRecoveryAt` only if the stored value and
  force count still match the pass's observed values
- treat a mismatch as a successful no-op so a startup/mutation force is not
  erased

## Expiration Purger

Cosmos implementation is no-op. TTL handles physical deletion.

The worker still calls the purger through the shared interface so SQL and Cosmos share the same worker flow.

The no-op purger does not mean lifecycle cleanup is a no-op. The consistency
recovery phase queries due lifecycle records. It point-reads each list and either
reschedules from the current expiry or, on 404, pages the list's recovery
partition and repairs every edge. Its generation-bound keyset/checkpoint is
durable.

## Consistency Recovery Service

The Cosmos implementation exposes a provider-specific recovery service behind
the provider-neutral `IConsistencyRecoveryService`; SQL returns an empty
`ConsistencyRecoveryPassResult`. The worker supplies the page/item/RU budget. A
pass separately queries:

- lists with `membershipRecoveryPending = true`;
- channels with `projectionRecoveryPending = true`;
- due candidate/retry/poison edge records; and
- due lifecycle records.

Queries use the exact total orders and composite indexes in
[`implementation-contracts.md`](implementation-contracts.md): list work by
`(membershipRecoveryDueAt,id)`, projection work by
`(projectionRecoveryDueAt,id)`, edge work by
`(nextAttemptAt,listId,id)`, and lifecycle work by
`(nextCheckAt,listId,id)`. Each uses `MaxItemCount = 25`, a maximum of 100
processed items, and a measured 2,000-RU scheduling budget per pass. Stop adding
work when either limit is reached; one in-flight item, including one ETag retry,
is the maximum RU overshoot.

Provision both the selective filter-leading composites and composites matching
the actual `ORDER BY` tuples. Emulator validation showed that Cosmos does not use
a composite with equality-filter paths prepended as a substitute for the query
order.

Per-kind cursor records in the recovery container's reserved `__system`
partition hold fixed-cycle timestamps and full keyset tuples. They wrap only at
end-of-cycle, ensuring restart fairness. Generic startup-immediate scheduling
and these global cursors are Task 2110. Task 2120 adds only lifecycle deadlines
and per-list cleanup checkpoints. Projection progress is the generation-bound
list-id keyset on the channel.

A shared cross-kind ticket cursor rotates
`Membership -> Projection -> EdgeDue -> LifecycleDue`. Before each page, the
worker durably advances the ticket to the successor; after RU exhaustion the
next pass therefore starts with that successor. A crash after ticket advance can
waste one page opportunity but not starve the kind, which returns within four
admitted tickets. EdgeDue's per-kind keyset prevents continuously replenished
normal edges from starving poison entries.

Claims use ETag-protected owner and lease fields. Expired claims are reclaimable,
duplicate processing is harmless, and completion checks the observed
membership/projection version or recovery generation. Failures back off with
jitter from one minute to one hour. Ten failures mark poison and emit an error,
but poison work remains durable and retries daily.
Membership and projection failures persist that state directly on their list or
channel document (`*RecoveryAttempt`, `*RecoveryPoison`, due timestamp, and
sanitized `*RecoveryLastErrorClass`). Successful convergence conditionally
clears those fields and measures latency from `*RecoveryStartedAt`.

Claims, edge/lifecycle transactional batches, checkpoints, cursor writes, and
target list/channel writes each make two total ETag attempts. A second conflict
leaves work due. A worker-state observed-generation mismatch is a successful
no-op rather than permission to overwrite a newer force.

Deleted-list cleanup includes all active edge states in deterministic
`(channelId,id)` order. Edge retirement and lifecycle count/generation changes
are transactional. The worker adopts the expected generation returned by its own
retirement and continues its keyset; only an external mismatch restarts.
An empty page triggers a from-start active-edge verification and conditional
zero-count/generation completion; leased, poison, or newly created edges block
completion.

Membership traversal has a separate membership keyset. It atomically adopts the
exact `edgeGeneration` returned by its own candidate-retirement batch and
continues. Any unexpected generation, including a concurrent other-instance
retirement, clears that keyset and restarts from the beginning.

With no backlog or throttling, polling makes new work eligible within one minute.
An expired lifecycle item is rechecked every ten minutes while its list still
exists. Alert on ordinary work older than 15 minutes and on cleanup incomplete
15 minutes after the first list 404; Cosmos TTL's physical-deletion delay is
outside the application SLO.

The deadline is never proof of deletion. Every due lifecycle first point-reads
the list. A present list moves the deadline to its authoritative expiry (or ten
minutes later when already expired), clears deletion-cleanup state, and releases
the lease. A 404 records the first observation time and starts the separate
generation-bound cleanup keyset. Explicit `Deleting` state is the exception: if
the list remains present, recovery first verifies all current membership edges
and resumes its ETag-conditional delete.

Cleanup deletes each edge in the same partition transaction that decrements the
count, increments the generation, and adopts the returned generation/checkpoint.
After a short page it rereads the lifecycle and list, queries active edges again
from the beginning, and requires both an empty query and a zero count before an
ETag-conditional lifecycle delete. A count disagreement emits error/poison
evidence and runs a generation-bound recount over only the at-most-125-edge list
partition. A concurrent list recreation or membership re-add is reread before
retirement; its channel reference is retained, orphan state/TTL is cleared, and
the lifecycle returns to `Active`.

Production retention remains unchanged at seven days for orphan channels. The
emulator fixture can inject a shorter internal retention only to prove eventual
physical TTL deletion with a bounded poll.

Repeated 404 observations within one missing episode preserve the lifecycle
attempt count and first-observed timestamp. Failures therefore use the standard
one-minute-to-one-hour backoff, reach poison on attempt ten, and retry daily;
successful drift recounts release their lifecycle lease so the corrected
zero-count record can immediately proceed to completion. Metrics expose
`recovery.lifecycle.cleanup_age` and
`recovery.lifecycle.cleanup_overdue`, and the overdue transition emits an
actionable warning containing list id and first-404 time but never the list
token.

Log and measure pending/poison counts, attempts, successes, conflicts, list 404s,
orphan transitions, oldest age, convergence latency, request charge, per-pass
items/RU, and expired-lease claims. Never log list tokens or Cosmos credentials.

## Tests

Cosmos provider tests should run against the local Cosmos emulator and be opt-in:

```text
YOUTUBED_RUN_COSMOS_TESTS=true
```

Tests should cover:

- point-read list read-model behavior
- daily renewal TTL updates
- ETag conflict retry for list membership/projection writes
- channel reverse reference and subscription count updates
- stale lookahead query shape
- share link consume concurrency
- worker state forced refresh and completion conflict handling
- failure after each durable add/remove/delete/projection side effect
- restart with expired recovery leases and persisted keysets
- concurrent mutation/recovery and genuine multi-instance claims
- explicit and TTL list deletion cleanup for active and unavailable channels
- fixed document/cardinality ceilings and per-pass item/RU bounds
- projection `[A,B]`, process `A`, concurrently remove `A`, then prove generation
  restart still processes `B`
- active-edge cap/count/generation transactions, poison/lease-aware lifecycle
  completion, cursor wrap fairness, and emulator query-plan/index use
- forced-RU cross-kind rotation with continuous Membership work, proving
  Projection, EdgeDue poison, and LifecycleDue tickets remain bounded across
  restart
- membership keyset continuation after its own expected retirement generation
  and from-start restart after another instance retires an edge
