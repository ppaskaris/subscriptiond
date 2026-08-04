# Cosmos DB Schema Plan

The Cosmos DB provider optimizes for minimal RU usage on the common list page read.

## Containers

The provider uses five containers:

- `lists`
- `channels`
- `shareLinks`
- `system`
- `recovery`

## Lists Container

Partition key:

```text
/id
```

The list document combines list settings and render projection:

```json
{
  "id": "list-guid",
  "token": "raw-secret-token",
  "title": "My list",
  "playbackRate": 1.0,
  "expiredAfter": "2026-06-24T12:00:00Z",
  "expirationRenewedOn": "2026-05-09",
  "ttl": 3974400,
  "membershipVersion": 7,
  "membershipRecoveryPending": true,
  "membershipRecoveryDueAt": "2026-05-09T12:01:00Z",
  "membershipRecoveryStartedAt": "2026-05-09T12:00:00Z",
  "membershipRecoveryAttempt": 2,
  "membershipRecoveryPoison": false,
  "membershipRecoveryLastErrorClass": "CosmosException",
  "channels": [
    {
      "id": "UC...",
      "url": "https://www.youtube.com/channel/UC...",
      "title": "Channel title",
      "thumbnail": "https://...",
      "staleAfter": "2026-05-09T13:00:00Z",
      "status": "Active",
      "statusReason": null,
      "statusUpdatedAt": null,
      "videos": [
        {
          "id": "video-id",
          "title": "Video title",
          "durationTicks": 123456789,
          "publishedAt": "2026-05-09T12:00:00Z",
          "thumbnail": "https://..."
        }
      ]
    }
  ]
}
```

`channels[].id` is the canonical membership list for a list. There is no separate `channelIds` array.

List reads should point-read by id and partition key. Metadata-only reads can project selected fields if measurement shows that helps RU usage.

List writes should use ETag optimistic concurrency. If a worker update conflicts with a user add/remove, the worker must re-read and reapply its patch.

Projection updates can use Cosmos patch to set fields such as `/channels`, but correctness still requires ETag or equivalent concurrency handling.

`membershipVersion` increments and `membershipRecoveryPending` becomes true in
the same ETag-protected replace that changes `channels[]`. Recovery clears the
flag only for the version it has completely reconciled. These scalar fields keep
pending work bounded independently of request volume.
The same write initializes `membershipRecoveryStartedAt`, resets
`membershipRecoveryAttempt` and `membershipRecoveryPoison`, and clears
`membershipRecoveryLastErrorClass`. A failed recovery conditionally increments
the attempt, stores only the sanitized exception class, and moves
`membershipRecoveryDueAt` through bounded exponential backoff. Attempt ten sets
the poison flag and a daily due time; successful convergence clears all failure
fields and records latency from the started timestamp.

## List Projection Sizing

List projections use one sizing policy for both add-channel seeding and worker
projection replacement. For a list with `channelCount` channels, each projected
channel is sorted by `publishedAt DESC, id ASC`, duplicate video ids are removed,
and the provider retains:

```text
all available videos published within the last 5 days
plus older videos until the channel has at least
min(100, max(5, ceil(ListRenderMaxItems / channelCount * 1.33))) videos
```

The availability qualifier reflects the canonical channel-document ceiling of the
newest 100 videos. Channel subdocuments are ordered by channel id so repeated
projection writes serialize deterministically. The final list-page behavior is
unchanged: the provider maps the hierarchy, the application globally sorts newest
first (video id breaks timestamp ties), and renders 100.

The supported projection envelope is:

- at most 100 channels in one list;
- at most 100 canonical videos supplied by one channel;
- at most 500 embedded projected videos across a list;
- a serialized UTF-8 list document strictly below 1,900,000 bytes.

The byte ceiling leaves a 197,152-byte safety margin below the Cosmos DB for NoSQL
2-MiB item limit. It is checked locally before every list replace, including
membership, settings, renewal, and worker projection writes. The cardinality
limits and byte check are both required because unusually long metadata can make a
nominally valid channel/video count too large.

Sizing always produces a fresh projected-channel/video DTO graph. A refreshed
channel DTO is never trimmed in place, so fan-out to differently sized lists and
an ETag retry after a membership change each recalculate from the complete
canonical input.

Removing a channel can increase the remaining per-channel allocation. Before the
membership replace, the Cosmos list repository point-reads only remaining
channels whose embedded distinct-video count is below the new allocation and
rehydrates those projections from canonical channel documents. Unavailable
channels participate in this repair because they remain visible until user
removal. Already-full projections incur no canonical read. The normal ETag retry
rereads membership and recomputes the required allocation and hydration set. If
canonical rehydration would cross the projected-video or serialized-byte ceiling,
the same ETag attempt falls back to the already bounded embedded projections
after removing the requested membership. A missing canonical channel likewise
keeps its embedded projection. Rehydration is therefore best-effort enrichment:
capacity pressure or a missing canonical document cannot prevent a user from
shrinking the list. Reverse-reference cleanup follows a successful membership
replace, or a read that confirms the membership or list is already absent, so
repeated removals safely repair stale references.

The recent-window guarantee applies inside the supported envelope. If adding a
channel would cross the channel, projected-video, or byte limit, the list document
and channel reverse reference are left unchanged and the add-channel form displays
the capacity error. If a worker refresh would cross the projected-video or byte
limit, no oversized list write is sent: the list continues to render its last
bounded projection and the worker logs the failed refresh attempt. Removing a
channel remains available so a user can return the list to the supported envelope.

The application installs one `CosmosSerializer` implementation on the production
SDK client and uses that exact serializer instance for the pre-write byte count.
The emulator fixture uses the same serializer, eliminating differences in naming,
escaping, null handling, and date formatting at the size boundary.

Emulator budget guards for a near-ceiling, maximum-cardinality representative list
are 350 RU for a point read and 3,000 RU for a projection-shaped replacement. These are
regression ceilings, not expected typical charges; actual charges are recorded by
the opted-in Cosmos suite.

The Cosmos provider maps embedded projected channels and videos to the hierarchical domain read model. The list page render procedure flattens that hierarchy in memory, sorts newest first, and renders 100.

The exact total video count is not stored.

## Channels Container

Partition key:

```text
/id
```

Canonical channel document:

```json
{
  "id": "UC...",
  "url": "https://www.youtube.com/channel/UC...",
  "title": "Channel title",
  "thumbnail": "https://...",
  "playlistId": "UU...",
  "staleAfter": "2026-05-09T13:00:00Z",
  "status": "Active",
  "statusReason": null,
  "statusUpdatedAt": null,
  "subscribedListIds": ["list-guid"],
  "subscriptionCount": 1,
  "subscriptionGeneration": 9,
  "orphanedAfter": null,
  "ttl": -1,
  "projectionVersion": 12,
  "projectionRecoveryPending": true,
  "projectionRecoveryDueAt": "2026-05-09T12:01:00Z",
  "projectionRecoveryStartedAt": "2026-05-09T12:00:00Z",
  "projectionRecoveryAttempt": 2,
  "projectionRecoveryPoison": false,
  "projectionRecoveryLastErrorClass": "CosmosException",
  "projectionRecoveryProjectionVersion": 12,
  "projectionRecoverySubscriptionGeneration": 9,
  "projectionRecoveryAfterListId": "earlier-list-guid",
  "videos": [
    {
      "id": "video-id",
      "title": "Video title",
      "durationTicks": 123456789,
      "publishedAt": "2026-05-09T12:00:00Z",
      "thumbnail": "https://..."
    }
  ]
}
```

Canonical `videos` are capped at the newest 100 videos, filtered by existing video max-age rules.

`subscribedListIds` is the materialized reverse-reference set.
`subscriptionGeneration` increments in the same ETag-protected write whenever
that normalized set changes. `subscriptionCount` is an indexed query helper and
repair target.

Channel updates must use optimistic concurrency when modifying `subscribedListIds` and `subscriptionCount`.

Unavailable channels remain until user removal and do not refresh.

Orphan channels receive TTL only when `subscriptionCount == 0` and no valid list references remain. Adding a channel to a list clears orphan markers and disables TTL.

Canonical refresh writes increment `projectionVersion` and set
`projectionRecoveryPending` atomically with refreshed canonical fields. Recovery
clears the flag only after the same version has been projected or its dead
references have been activated for membership repair.
The refresh also initializes the projection started timestamp and resets its
attempt, poison, and sanitized error-class fields. Projection failures persist
the same bounded backoff/ten-attempt poison semantics as membership work.
Successful completion clears those fields and reports convergence latency.
`projectionRecoveryAfterListId` is a sorted-list-id keyset, not an integer
offset, and is valid only while both bound generation fields equal the current
`projectionVersion` and `subscriptionGeneration`. Either change clears the key
and restarts from the beginning.

Channel writes use the same serializer preflight as list writes and must remain
strictly below 1,900,000 UTF-8 bytes. Add membership reserves the reverse
reference before committing the list, so an add that would exceed this ceiling
is rejected without creating successful list membership.

## ShareLinks Container

Partition key:

```text
/id
```

`id` is the share password.

```json
{
  "id": "four-word-password",
  "listId": "list-guid",
  "createdAt": "2026-05-09T12:00:00Z",
  "expiresAfter": "2026-05-09T13:10:00Z",
  "usedAt": null,
  "ttl": 90000
}
```

Share links store only `listId`, not the list token.

Consume flow:

1. point-read share link by password
2. reject if missing, expired, or used
3. point-read target list
4. reject if missing
5. patch/replace `usedAt` with ETag
6. redirect with list token

TTL deletes share links after `ExpiresAfter + ShareLinkRetentionAfterExpiration`.
The application calculates the integer TTL from its `IAppClock` immediately
before persistence; Cosmos then counts that value from the successful write's
server `_ts`. Upward rounding, application/server clock skew, and write latency
can shift eligibility around the intended absolute deadline, and physical
deletion remains asynchronous. Expected emulator bounds, production latency
guidance, mutation rules, and reconciliation alerts are defined under
**TTL operation and alert timing** in
[`cosmos-implementation-sketch.md`](cosmos-implementation-sketch.md).

Querying share links by list id is indexed but cross-partition. This is acceptable initially because share management is low volume.

## System Container

Partition key:

```text
/id
```

Scheduler document:

```json
{
  "id": "scheduler",
  "nextChannelRefreshAt": "2026-05-09T13:00:00Z",
  "channelRefreshForceCount": 0,
  "nextPurgeAt": "2026-05-09T13:10:00Z",
  "nextConsistencyRecoveryAt": "2026-05-09T12:01:00Z",
  "consistencyRecoveryForceCount": 3
}
```

`nextConsistencyRecoveryAt` schedules the provider-neutral worker phase. SQL has
no recovery work and may advance it normally; Cosmos queries the durable due
indexes described below. Correctness does not depend on this timestamp because
pending records survive and every application start forces an initial pass.
Forcing sets the time sentinel and increments `consistencyRecoveryForceCount`;
completion advances the schedule only if both observed fields still match.

## Recovery Container

Partition key:

```text
/listId
```

Lifecycle records are point-addressable as `(id = "lifecycle", listId)`:

```json
{
  "id": "lifecycle",
  "listId": "list-guid",
  "kind": "Lifecycle",
  "state": "Active",
  "expiredAfter": "2026-06-24T12:00:00Z",
  "nextCheckAt": "2026-06-24T12:00:00Z",
  "activeEdgeCount": 1,
  "edgeGeneration": 14,
  "membershipEdgeAfterChannelId": null,
  "membershipEdgeAfterId": null,
  "membershipTraversalEdgeGeneration": null,
  "membershipVersionBeingRepaired": null,
  "cleanupEdgeAfterChannelId": null,
  "cleanupEdgeAfterId": null,
  "cleanupTraversalEdgeGeneration": null,
  "missingObservedAt": null,
  "owner": null,
  "leaseUntil": null,
  "attempt": 0,
  "nextAttemptAt": "2026-06-24T12:00:00Z",
  "lastErrorClass": null
}
```

There is one deterministic edge record per candidate pair. The id encodes a
stable hash/escaped form of `channelId`, while the full channel id remains a
property:

```json
{
  "id": "edge:stable-channel-key",
  "listId": "list-guid",
  "kind": "Edge",
  "channelId": "UC...",
  "active": true,
  "state": "Tracked",
  "generation": 4,
  "owner": null,
  "leaseUntil": null,
  "attempt": 0,
  "nextAttemptAt": null,
  "lastObservedMembershipVersion": 7,
  "lastErrorClass": null
}
```

`state` is `Candidate`, `Tracked`, `Due`, `Retiring`, or `Poison`. Every edge
document is active recovery evidence and has no TTL. Successful retirement
deletes it in the same transactional batch that updates the lifecycle; no
inactive diagnostic edge copy is retained. Mutation ownership is a bounded
lease; a request may not commit list membership after losing it.

Lifecycle records contain no membership array. Edge documents are fixed shape
and must be smaller than 16 KiB. `activeEdgeCount` and `edgeGeneration` change in
the same list-partition transactional batch as active edge creation/retirement.
A supported list has at most 100 tracked memberships and 125 total active edges,
including candidates, poison, retiring, and leased work. The 126th distinct
active candidate is rejected, so repeated failed adds cannot grow the partition
without bound. Because retirement deletes the document, this is a total edge
document bound, not only an outstanding-work counter. Candidate ids are
deterministic per pair, so retries and duplicate requests coalesce.

Task 2110 membership reconciliation uses the `membership*` keyset/version
fields. When its own candidate-retirement batch returns the exact next
`edgeGeneration`, it atomically adopts that value and continues its keyset. Any
unexpected/external generation mismatch—including another instance's
retirement—clears the membership keyset and restarts from the beginning.
Task 2120 deleted-list cleanup uses only the separate `cleanup*` keyset
bound to `cleanupTraversalEdgeGeneration` and orders all active states by
`(channelId, id)`. It adopts the expected next generation returned by its own
transactional retirement and continues from its keyset; only an
unexpected/external generation change restarts from the beginning. Cleanup may
complete only after `activeEdgeCount == 0`, a fresh from-start active-edge query
is empty, and a conditional lifecycle update succeeds. Leased/poison/new
candidates therefore cannot be skipped.

`missingObservedAt` is set exactly once when a lifecycle point read first sees
the list's 404. It anchors cleanup-latency metrics and the 15-minute alert; it is
cleared whenever a current list is observed. Successful completion conditionally
deletes the lifecycle document itself. Because edge creation conditionally
updates that same lifecycle item, completion cannot race past a new or
reactivated edge.

The first 404 also starts one durable cleanup failure episode. Reobserving the
same missing list preserves `attempt`, `lastErrorClass`, and the original
`missingObservedAt`; it does not reset backoff. Only observing a present list or
starting a genuinely new missing episode clears those fields. This lets a
persistent cleanup failure reach attempt ten, emit poison evidence, and retry
daily while keeping its SLO age anchored to the first 404.

Explicit deletion marks the lifecycle `Deleting`, sets `nextCheckAt` due, and
seeds/verifies every current membership edge before the list's ETag-conditional
delete. The request still attempts immediate reverse-reference repair for its
observed channels, while the durable lifecycle/edges make every interruption
restartable. A recovery instance that finds `Deleting` with the list still
present repeats seeding before retrying the conditional delete.

This container is a recovery index, not membership authority. Every processor
point-reads the list before updating a channel. Creating edges before add,
retaining them while membership exists, and checking a lifecycle record after
the list's expiry lets TTL cleanup find both missing and dead reverse references
without querying all channels or all lists.

The reserved `listId = "__system"` partition contains one cursor per global work
kind:

```json
{
  "id": "cursor:edge-due",
  "listId": "__system",
  "kind": "Cursor",
  "workKind": "EdgeDue",
  "cycleNow": "2026-05-09T12:00:00Z",
  "cycleGeneration": 8,
  "afterDueAt": null,
  "afterListId": null,
  "afterId": null,
  "updatedAt": "2026-05-09T12:00:00Z"
}
```

Cursor records use the total key for their work kind. They advance after examined
pages, wrap only after reaching the end by clearing the key/incrementing the
cycle, and hold `cycleNow` fixed until wrap. Work inserted behind a cursor waits
for the next wrap but cannot be starved by continuously inserted early work.

The reserved partition also has a cross-kind ticket cursor:

```json
{
  "id": "cursor:work-kind-rotation",
  "listId": "__system",
  "kind": "Cursor",
  "nextStartingKind": "Projection",
  "rotationGeneration": 42,
  "updatedAt": "2026-05-09T12:00:00Z"
}
```

Before each page, an ETag write returns the current kind and durably advances to
its successor in `Membership, Projection, EdgeDue, LifecycleDue` order. A crash
may waste that ticket but cannot roll the cursor back; the skipped kind is
offered again within four admitted tickets. A pass that exhausts RU stops after
the page, so the next pass begins with the persisted successor. EdgeDue's own
fixed-cycle cursor ensures its due poison records also progress.

All ETag-protected claim, transactional-batch, checkpoint, cursor, list, and
channel writes use an initial attempt plus one reread/retry. The second conflict
preserves due state for another pass. Conditional worker-state completion does
not overwrite an observed force-generation mismatch.

## Indexing

Indexing should be narrowed to reduce write RU.

Lists:

- point reads by id
- include `/membershipRecoveryPending`
- include `/membershipVersion`
- include `/membershipRecoveryDueAt`
- composite ascending:
  `/membershipRecoveryPending`, `/membershipRecoveryDueAt`, `/id`
- query-order composite ascending:
  `/membershipRecoveryDueAt`, `/id`
- exclude `/channels/*`

Channels:

- index `/staleAfter`
- index `/subscriptionCount`
- index `/status` if used in stale queries
- include `/projectionRecoveryPending`
- include `/projectionVersion`
- include `/subscriptionGeneration`
- include `/projectionRecoveryDueAt`
- composite ascending:
  `/projectionRecoveryPending`, `/projectionRecoveryDueAt`, `/id`
- query-order composite ascending:
  `/projectionRecoveryDueAt`, `/id`
- exclude `/videos/*`

ShareLinks:

- support `/listId`
- support `/createdAt`
- support `/expiresAfter`
- support `/usedAt`

System:

- minimal indexing

Recovery:

- include `/kind`
- include `/state`
- include `/nextAttemptAt`
- include `/nextCheckAt`
- include `/leaseUntil`
- include `/active`
- include `/channelId`
- composite ascending: `/kind`, `/active`, `/nextAttemptAt`, `/listId`, `/id`
- composite ascending: `/kind`, `/nextCheckAt`, `/listId`, `/id`
- composite ascending: `/kind`, `/active`, `/channelId`, `/id`
- query-order composite ascending: `/nextAttemptAt`, `/listId`, `/id`
- query-order composite ascending: `/nextCheckAt`, `/listId`, `/id`
- query-order composite ascending: `/channelId`, `/id`
- exclude all other paths after the exact composite/order requirements are
  verified against emulator query plans

The query-order forms are required in addition to the filter-leading forms:
emulator validation showed that equality-filter fields prepended to a composite
do not satisfy an `ORDER BY` tuple that omits those fields. Exact indexing policy
JSON should be written during Cosmos implementation and verified with emulator
tests.
