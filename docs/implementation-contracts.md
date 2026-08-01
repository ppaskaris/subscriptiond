# Implementation Contracts

This document sketches the provider-neutral contracts, configuration knobs, retry policy, and observability expectations for the SQL-first refactor and later Cosmos provider.

The signatures are intentionally approximate. Implementation can adjust names and parameters when the code shape is clearer, but the responsibilities should stay stable.

## Repository And Service Ports

### Lists

```csharp
public interface IListRepository
{
    Task<SubscriptionList?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AuthenticatedListAccess?> GetForAuthenticatedAccessAsync(
        Guid id,
        string token,
        CancellationToken cancellationToken);
    Task<ListChannelProjection?> GetChannelProjectionAsync(
        Guid id,
        CancellationToken cancellationToken);
    Task<ListVideoProjection?> GetVideoProjectionAsync(
        Guid id,
        CancellationToken cancellationToken);
    Task CreateAsync(SubscriptionList list, CancellationToken cancellationToken);
    Task UpdateSettingsAsync(Guid id, string title, decimal playbackRate, CancellationToken cancellationToken);
    Task AddChannelAsync(Guid listId, Channel channel, CancellationToken cancellationToken);
    Task RemoveChannelAsync(Guid listId, string channelId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
```

Authenticated access renews list expiration at most once per UTC day. Maintenance and projection reads do not renew expiration.

The normal authenticated list-page use case is exposed as one provider-neutral
operation that returns `ListVideoProjection`. SQL may compose its normalized
list and projection reads. Cosmos must point-read the list document once,
constant-time compare the route token, map the bounded video projection from
that document, and use its ETag for the renewal replacement when renewal is due.
A 412 permits one reread/reapply; a second 412 is surfaced. A 404 from the
renewal replacement is treated as concurrent deletion and returns no projection.
The list lifecycle record need not be synchronously renewed on this page path:
its old deadline is a safe early check, at which lifecycle recovery point-reads
the authoritative renewed list and reschedules it.

The Cosmos SDK pipeline records `list_page.requests` and
`list_page.request_charge` histograms, tagged by outcome. For a representative
list with one projected channel and video, the emulator regression budgets are
one request and at most 10 RU on the common same-day path, and two requests and
at most 25 RU on the renewal path. The existing maximum supported document
point-read ceiling remains 350 RU.

### Channels

```csharp
public interface IChannelRepository
{
    Task<Channel?> GetAsync(string id, CancellationToken cancellationToken);
    Task SaveDiscoveredAsync(Channel channel, CancellationToken cancellationToken);
    Task<IReadOnlyList<StaleChannelReference>> GetStaleLookaheadAsync(
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetNextActiveSubscribedRefreshAtAsync(
        CancellationToken cancellationToken);
    Task<IReadOnlyList<Channel>> GetBatchAsync(
        IReadOnlyCollection<string> channelIds,
        CancellationToken cancellationToken);
    Task SaveRefreshResultsAsync(
        IReadOnlyCollection<ChannelRefreshResult> results,
        CancellationToken cancellationToken);
    Task UpdateSubscriptionsAsync(
        string channelId,
        Func<IReadOnlyList<Guid>, IReadOnlyList<Guid>> update,
        CancellationToken cancellationToken);
}
```

The provider must keep `subscribedListIds` and `subscriptionCount` consistent. If optimistic concurrency fails, retry once, then throw.

`GetStaleLookaheadAsync` returns active subscribed channels whose stale time is due, ordered by stale time. The unified worker selects the first configured batch from that lookahead before YouTube work begins. `GetNextActiveSubscribedRefreshAtAsync` returns the next active subscribed channel stale time, or `null` when no active subscribed channel work is known.

### List Projection Updates

```csharp
public interface IListProjectionRepository
{
    Task UpdateProjectedChannelsAsync(
        IReadOnlyCollection<Channel> refreshedChannels,
        CancellationToken cancellationToken);
}
```

SQL implements this as no-op because SQL read models come from joins.

Cosmos point-reads affected list documents, replaces only the refreshed channel subdocuments, and writes with optimistic concurrency. If a conflict occurs, re-read and retry once, then throw.

### Share Links

```csharp
public interface IShareLinkRepository
{
    Task<bool> TryCreateAsync(ShareLink shareLink, CancellationToken cancellationToken);
    Task<IReadOnlyList<ShareLink>> GetByListAsync(Guid listId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid listId, string password, CancellationToken cancellationToken);
    Task DeleteByListAsync(Guid listId, CancellationToken cancellationToken);
    Task<ConsumedShareLink?> ConsumeAsync(string password, CancellationToken cancellationToken);
}
```

Consume verifies the target list exists before marking the share link used. The used update must be concurrency-protected.

### Worker State

```csharp
public sealed class WorkerState
{
    public DateTimeOffset? NextChannelRefreshAt { get; init; }
    public long ChannelRefreshForceCount { get; init; }
    public DateTimeOffset NextPurgeAt { get; init; }
    public DateTimeOffset NextConsistencyRecoveryAt { get; init; }
    public long ConsistencyRecoveryForceCount { get; init; }
}

public interface IWorkerStateStore
{
    Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken);
    Task ForceChannelRefreshAsync(CancellationToken cancellationToken);
    Task ForceConsistencyRecoveryAsync(CancellationToken cancellationToken);
    Task CompleteChannelRefreshPassAsync(
        DateTimeOffset? observedNextChannelRefreshAt,
        long observedChannelRefreshForceCount,
        DateTimeOffset? nextChannelRefreshAt,
        CancellationToken cancellationToken);
    Task CompletePurgeAsync(DateTimeOffset nextPurgeAt, CancellationToken cancellationToken);
    Task CompleteConsistencyRecoveryPassAsync(
        DateTimeOffset observedNextConsistencyRecoveryAt,
        long observedConsistencyRecoveryForceCount,
        DateTimeOffset nextConsistencyRecoveryAt,
        CancellationToken cancellationToken);
}
```

`ForceChannelRefreshAsync` sets `NextChannelRefreshAt = DateTimeOffset.MinValue`.

`CompleteChannelRefreshPassAsync` must not overwrite a forced refresh that happened during the worker pass. Providers should compare both the observed channel refresh time and an observed force generation/counter so a second force is not erased when the pass itself observed the forced sentinel value.

`ForceConsistencyRecoveryAsync` sets `NextConsistencyRecoveryAt` to
`DateTimeOffset.MinValue`, increments `ConsistencyRecoveryForceCount`, and pulses
the in-process wake signal. `CompleteConsistencyRecoveryPassAsync` moves the
schedule only when both observed recovery fields still match. A mismatch is a
successful no-op: pending durable work or a newer force remains due.

### Consistency Recovery

```csharp
public interface IConsistencyRecoveryService
{
    Task<ConsistencyRecoveryPassResult> RecoverAsync(
        ConsistencyRecoveryPassBudget budget,
        CancellationToken cancellationToken);
}

public sealed record ConsistencyRecoveryPassBudget(
    int PageSize,
    int MaxItems,
    double RuSchedulingBudget);

public sealed record ConsistencyRecoveryPassResult(
    int Examined,
    int Claimed,
    int Succeeded,
    int Failed,
    int Poison,
    double RequestCharge,
    bool HasMoreEligibleWork,
    DateTimeOffset? NextEligibleAt);
```

The SQL implementation returns an empty result. Cosmos processes the four
indexed work kinds described below. The worker schedules another immediate pass
when `HasMoreEligibleWork` is true; otherwise it schedules the earlier of
`NextEligibleAt` and `clock.UtcNow + ConsistencyRecoveryPollInterval`.
Application startup calls `ForceConsistencyRecoveryAsync`, so a durable backlog
is examined immediately after every restart.

### Expiration Purger

```csharp
public interface IExpirationPurger
{
    Task<int> PurgeExpiredListsAsync(CancellationToken cancellationToken);
    Task<int> PurgeExpiredShareLinksAsync(CancellationToken cancellationToken);
    Task<int> PurgeExpiredChannelsAsync(CancellationToken cancellationToken);
}
```

SQL deletes expired data and returns the number of deleted rows. Cosmos no-ops and returns `0` because TTL handles physical deletion.

## Conflict Policy

Provider operations that use optimistic concurrency should make two total attempts:

1. initial attempt
2. one retry after re-reading current state

If the retry fails, throw. Production can relax this later if conflicts prove common.

This policy applies to:

- list membership updates
- Cosmos projection document writes
- channel subscription array/count updates
- recovery claims, edge/lifecycle transactional batches, checkpoints, and
  global cursor writes
- share-link consume updates
- worker state channel completion when protected by observed state
- worker state consistency-recovery completion when protected by observed state

An observed-state mismatch on a worker completion method is an intentional no-op,
not a conflict to overwrite. For recovery documents, the second conflict leaves
the work eligible/durable for a later pass; a processor never loops without
bound inside one item.

## Configuration Knobs

Start with these settings as constants or options. Provider selection belongs in configuration.

```text
Persistence.Provider = SqlServer | Cosmos

ChannelRefreshBatchSize = 10
ChannelRefreshLookaheadMultiplier = 10
ChannelRefreshLookaheadCount = 100
YoutubeCallDelay = 2 seconds
PurgeInterval = 10 minutes

ListRenderMaxItems = 100
ListProjectionRecentVideoAge = 5 days
ListProjectionPerChannelMin = 5
ListProjectionOversamplingFactor = 1.33

CosmosListMaxChannels = 100
CosmosListMaxProjectedVideos = 500
CosmosListSerializedSizeSafetyCeiling = 1,900,000 UTF-8 bytes
CosmosListPointReadRuBudget = 350
CosmosListProjectionWriteRuBudget = 3,000

ChannelLookupCacheDuration = 24 hours
ChannelLookupCacheSizeLimit = 1000

ChannelUnavailableStaleDelay = 100 years
ChannelOrphanRetention = 7 days

ConsistencyRecoveryPollInterval = 1 minute
CosmosConsistencyRecoveryBatchSize = 25
CosmosConsistencyRecoveryMaxItemsPerPass = 100
CosmosConsistencyRecoveryRuBudgetPerPass = 2000
CosmosConsistencyRecoveryLeaseDuration = 2 minutes
CosmosConsistencyRecoveryPoisonAttemptCount = 10
CosmosLifecycleExpiryRecheckInterval = 10 minutes
CosmosRecoveryDocumentSizeCeiling = 16,384 UTF-8 bytes
CosmosRecoveryMaxActiveEdgesPerList = 125
CosmosChannelSerializedSizeSafetyCeiling = 1,900,000 UTF-8 bytes
```

The Cosmos list projection sizing knobs form one invariant rather than independent
best-effort settings. Both membership seeding and worker refreshes retain every
available video in the five-day recent window, then retain older videos until each
channel has at least
`min(100, max(5, ceil(ListRenderMaxItems / channelCount * 1.33)))` entries.
Channels sort by id; videos sort by publication descending and id ascending.
Duplicate video ids are removed.

The supported envelope is 100 channels, 100 canonical videos per channel, 500
projected videos per list, and a serialized list item strictly below 1,900,000
UTF-8 bytes. A provider must validate the complete item before sending a write.
Exceeding any bound throws `ListCapacityExceededException` without attempting the
oversized write. Add-channel callers surface that exception as a form error;
worker projection callers retain the last successfully bounded projection and log
the failed pass. The rendered global 100-video limit and stale-channel rules do
not change.

Projection selection must return a fresh DTO graph rather than trimming input
documents. The same refreshed channel may fan out to lists with different channel
counts, and an ETag retry may observe a changed count; every attempt recalculates
from the unmodified canonical projection.

When removing membership increases the allocation for remaining channels, Cosmos
point-reads only underfilled canonical channels and rehydrates them before the
conditional list replace. This includes unavailable channels. An ETag retry
recomputes the hydration set from the newly read membership. If the hydrated
candidate exceeds either list capacity bound, the provider completes the same
conditional removal with the existing embedded projections instead. A canonical
404 also retains the embedded projection. Reverse-reference repair runs after
the removal version is written successfully, or after a read confirms that the
membership or list is already absent.

The preflight UTF-8 measurement and Cosmos SDK writes must use the same configured
`CosmosSerializer`. Provider and emulator clients install the shared serializer
instead of relying on SDK defaults or separately configured serializer options.

## Cosmos Consistency And Recovery Contract

### Authority And Converged State

For a list `L` and channel `C`, authoritative membership is:

```text
M(L,C) = the lists/L document exists and contains C in channels[]
```

No channel document, recovery record, projection, cached value, or scheduled work
can create membership. Once recovery is quiescent:

- `C.subscribedListIds` is the sorted, distinct set of list ids for which
  `M(L,C)` is true.
- `C.subscriptionCount == C.subscribedListIds.Count`.
- If the count is positive, `orphanedAfter == null` and `ttl == -1`.
- If the count is zero, `orphanedAfter` is set once when the channel becomes an
  orphan and `ttl` targets `orphanedAfter + ChannelOrphanRetention`.
- A list's embedded channel fields and videos are only its render projection.
- Recovery edge and lifecycle records are durable indexes/work, not authority.

The list item has scalar `membershipVersion` and
`membershipRecoveryPending` properties. A membership-changing conditional write
increments the version and sets the pending flag atomically with `channels[]`.
It also initializes `membershipRecoveryStartedAt` and resets
`membershipRecoveryAttempt`, `membershipRecoveryPoison`, and
`membershipRecoveryLastErrorClass`. A failed repair conditionally increments
the attempt, stores a sanitized error class, and advances the due timestamp
through bounded backoff; attempt ten marks poison and remains daily retryable.
Successful convergence clears the failure fields and measures latency from the
started timestamp.
The channel item has the analogous scalar `projectionVersion` and
`projectionRecoveryPending` fields, set atomically with a canonical refresh. It
also has `subscriptionGeneration`, incremented in the same ETag-protected write
whenever the normalized `subscribedListIds` set changes. Projection progress and
completion are conditional on both the observed `projectionVersion` and
`subscriptionGeneration`. There are no unbounded pending-operation arrays in
either item.
Projection work has analogous `projectionRecoveryStartedAt`,
`projectionRecoveryAttempt`, `projectionRecoveryPoison`, and
`projectionRecoveryLastErrorClass` fields with the same durable failure,
daily-poison-retry, and successful-clear semantics.

The Cosmos-only `recovery` container has partition key `/listId`. It contains:

- one lifecycle record per list, created before the list and never given TTL
  while the list may exist; and
- one deterministic edge record per candidate `(listId, channelId)`, created
  before an add can commit and retained while that membership exists; and
- a small cursor record per global due-work kind in the reserved `__system`
  partition, used to rotate fairly through a backlog across passes and restarts.

An active lifecycle record contains only list-level scheduling/checkpoint data,
not a growing channel-id array. It maintains `activeEdgeCount` and
`edgeGeneration`. Creating a new active edge or retiring an active edge uses a
Cosmos transactional batch in that list partition to conditionally update the
lifecycle record and edge together; `edgeGeneration` increments for either set
change. Edge state/lease/poison changes do not change the count. Edge records are
fixed-shape documents.

A normal list has at most one lifecycle record and 100 tracked edges, matching
the list cardinality. The transactional counter rejects creation above 125 total
active edges, bounding the 100 memberships plus failed distinct candidates.
Callers get a retryable recovery-capacity error and may retry after reconciliation
retires absent candidates. Failed candidates are deterministic per pair, are
reconciled after the request lease expires, and are retired only after the list
is authoritatively found not to contain the channel. The retirement batch
deletes the edge document; logs and metrics carry diagnostics instead of retained
inactive copies. Thus repeated failed distinct adds cannot accumulate documents
behind the 125-edge cap. Each recovery document must serialize below 16 KiB.

### Mutation And Projection Protocols

An add uses this order:

1. Get or create the lifecycle record and upsert the deterministic edge as a
   candidate with a mutation generation and bounded owner lease.
2. Add the provisional reverse reference to the channel with ETag protection.
   This reserves channel-document capacity and clears orphan/TTL state. Reject
   the add before list membership commits if the channel would reach the
   1,900,000-byte safety ceiling.
3. Conditionally write the list membership, increment `membershipVersion`, and
   set `membershipRecoveryPending = true`.
4. Mark the edge tracked, repair the channel from a fresh list read, and
   conditionally clear the pending flag only if the membership version still
   equals the repaired version.

The request must not perform step 3 after its edge owner lease expires. Cosmos
request timeouts are shorter than the lease; a request renews and conditionally
verifies its edge generation before committing or abandons the attempt. A worker
does not claim an unexpired request lease. This prevents an old request from
committing after recovery has retired its candidate.

A remove first ensures the edge exists, then conditionally removes the list
membership and sets the same version/pending fields. It next rereads the list,
removes the reverse reference with ETag protection, applies orphan/TTL state,
and retires the edge. A repeated remove still activates repair, even when the
list membership is already absent.

Recovery never blindly replays an "add" or "remove" command. It point-reads the
list and makes only that `(listId, channelId)` entry in the channel agree with
the current `M(L,C)`, preserving unrelated references. The channel's count and
orphan fields are recalculated in the same conditional channel write. It then
updates or retires the edge. A list pending flag is cleared with an ETag only
after every edge page for the observed membership version is repaired; a changed
version restarts the bounded traversal. This makes reordered or duplicate work
idempotent and makes the latest list version win.

Task 2110 membership traversal snapshots lifecycle `edgeGeneration` and pages
active edges in `(channelId,id)` keyset order. When that same worker
transactionally retires an absent candidate, the batch returns the known
generation increment; it atomically persists/adopts that exact generation with
the membership keyset and continues. If the lifecycle generation differs for
any other reason—including another worker retiring an edge or a concurrent
candidate create/reactivation—the worker clears the membership keyset and
restarts from the beginning. It never treats an arbitrary larger generation as
its own expected increment.

List deletion and membership mutation still use the list ETag as their final
serialization point. If an add/remove wins first, delete gets 412, rereads, and
reseeds the changed membership before retrying. If delete wins first, the
membership replace gets 404/412 and cannot commit; its candidate/provisional
state is later repaired as absent.

A canonical refresh increments `projectionVersion` and sets
`projectionRecoveryPending` in the channel write. Projection recovery reads the
current channel, visits its bounded reverse references, point-reads each list,
and either conditionally updates the embedded projection or activates the
corresponding edge for membership repair. It clears the channel flag only if the
projection version and subscription generation are unchanged. Projection writes
must preserve list recovery fields when an ETag retry rereads a list.

Projection fan-out sorts distinct list ids ascending and persists a keyset
`projectionRecoveryAfterListId`, bound to
`projectionRecoveryProjectionVersion` and
`projectionRecoverySubscriptionGeneration`. It processes ids strictly greater
than the key, never an integer array offset. If either generation changes, it
clears the key and restarts from the beginning of the current reference set.
Completion rereads the channel and conditionally clears pending only when both
generations still match and a from-start/current-set check has no unprocessed
list id. Thus processing `A` from `[A,B]` followed by concurrent removal of `A`
cannot turn offset `1` into a skipped `B`; the subscription-generation change
forces a restart and `B` is processed.

An authenticated list renewal updates the list TTL normally and then advances
the lifecycle record's `expiredAfter`/`nextCheckAt`. If the second write fails,
the old, earlier check is harmless: the lifecycle worker point-reads the list,
observes that it still exists, and refreshes the lifecycle deadline. It never
infers deletion from a clock deadline alone.

### Explicit Deletion And TTL

Explicit deletion first marks the lifecycle record `Deleting`, reads the list,
and ensures an edge record exists for every channel currently embedded in the
list. Only after all at-most-100 edge upserts succeed may it conditionally delete
the list. A retry that finds the list still present returns the lifecycle record
to active state or retries the delete; a retry that gets 404 proceeds with
cleanup.

For automatic TTL deletion, active lifecycle records become due at the last
known `expiredAfter`. A due check always point-reads the list:

- if it exists, copy its current expiry into the lifecycle record and schedule
  the next check; Cosmos TTL lag and a failed renewal-mirror write are harmless;
- if it returns 404, page the recovery partition and repair every edge as absent.

Deleted-list edge traversal snapshots the lifecycle `edgeGeneration`, then
queries that logical partition in deterministic `(channelId ASC, id ASC)` order,
starting strictly after `(edgeAfterChannelId, edgeAfterId)`. It includes every
`active = true` edge regardless of Candidate/Tracked/Due/Retiring/Poison state,
`nextAttemptAt`, or another instance's lease. A currently leased edge is not
stolen, but it remains active and prevents lifecycle completion; the traversal
can advance past it and the from-start verification below will find it again.

After a channel write succeeds, retirement is a same-partition transactional
batch that conditionally deletes that edge, decrements `activeEdgeCount`,
increments `edgeGeneration`, and updates the lifecycle checkpoint. A stale batch
fails rather than retiring a reactivated generation. The batch returns the exact
expected next generation; the worker adopts that generation and continues from
its current keyset, so its own retirements do not force a restart. Only a
generation mismatch not produced by that claimed batch—new/reactivated work or
another instance's retirement—clears the keyset and restarts from the beginning.

An empty page is not completion. The worker clears the checkpoint, rereads the
lifecycle record, requires `activeEdgeCount == 0`, and performs a new
`TOP 1 ... ORDER BY channelId, id` query from the beginning over all active edge
states. Only when that query is empty does it conditionally complete/TTL the
lifecycle record with its observed ETag and generation. Edge creation is also a
transactional batch conditional on that lifecycle ETag/state. Therefore a new
candidate either commits first and makes completion fail, or completion commits
first and causes candidate creation to fail. Leased and poison edges keep the
count positive and can never be hidden by a cursor.

If `activeEdgeCount` disagrees with the from-start query, completion is
forbidden. The lifecycle is marked poison/alerting and a generation-bound
recount of that single, at-most-125-edge partition repairs the counter
conditionally. This is bounded data-drift repair, not readiness failure and not
a cross-account scan.

Creating the edge before an add, retaining it through membership, and seeding all
edges before explicit deletion means physical list deletion cannot erase the
last cleanup index. Unavailable channels are point-read by id and repaired by
this path; neither status nor `staleAfter` participates. Once the last edge is
repaired, the unavailable channel receives the same orphan TTL as an active
channel and therefore cannot be kept alive indefinitely by the deleted list.

The mechanism assumes its edge invariant from the moment it is enabled. A
deployment over pre-existing Cosmos data must first run a continuation-bounded
bootstrap/reconciliation that creates lifecycle and edge records; that data
migration is not part of this design task. Normal operation never performs an
all-list, all-channel, or account scan.

With no backlog or throttling, new pending work is polled within one minute.
Lifecycle checks start within one poll after the last known expiry, then repeat
every ten minutes while an expired item is still physically present. Cosmos TTL
deletion time itself has no application-controlled upper bound; the cleanup SLO
starts when a point read first returns 404. A supported 100-edge list fits in
four 25-item pages and is eligible to converge in one 100-item pass, subject to
the RU limit and conflicts. The hard 125-edge state including failed candidates
fits five pages and therefore at most two item-budget passes. Alert if ordinary
(non-poison) work is older than 15 minutes or a deleted-list cleanup is not
complete 15 minutes after its first 404. These are operational SLOs, not
permission to discard overdue work.

### Due Queries, Keysets, And Indexes

All queue queries use `MaxItemCount = 25`, select only lightweight claim fields,
and order by a total key. `@now` is fixed for a cursor cycle so newly delayed or
newly inserted work cannot move the cycle boundary:

```sql
-- lists container; keyset (membershipRecoveryDueAt, id)
SELECT c.id, c.membershipVersion, c.membershipRecoveryDueAt
FROM c
WHERE c.membershipRecoveryPending = true
  AND c.membershipRecoveryDueAt <= @now
  AND (c.membershipRecoveryDueAt > @afterDueAt
    OR (c.membershipRecoveryDueAt = @afterDueAt AND c.id > @afterId))
ORDER BY c.membershipRecoveryDueAt ASC, c.id ASC

-- channels container; keyset (projectionRecoveryDueAt, id)
SELECT c.id, c.projectionVersion, c.subscriptionGeneration,
       c.projectionRecoveryDueAt
FROM c
WHERE c.projectionRecoveryPending = true
  AND c.projectionRecoveryDueAt <= @now
  AND (c.projectionRecoveryDueAt > @afterDueAt
    OR (c.projectionRecoveryDueAt = @afterDueAt AND c.id > @afterId))
ORDER BY c.projectionRecoveryDueAt ASC, c.id ASC

-- recovery container edge queue; keyset (nextAttemptAt, listId, id)
SELECT c.id, c.listId, c.generation, c.nextAttemptAt
FROM c
WHERE c.kind = "Edge" AND c.active = true
  AND c.nextAttemptAt <= @now
  AND (c.nextAttemptAt > @afterDueAt
    OR (c.nextAttemptAt = @afterDueAt AND c.listId > @afterListId)
    OR (c.nextAttemptAt = @afterDueAt AND c.listId = @afterListId
      AND c.id > @afterId))
ORDER BY c.nextAttemptAt ASC, c.listId ASC, c.id ASC

-- recovery container lifecycle queue; keyset (nextCheckAt, listId, id)
SELECT c.id, c.listId, c.edgeGeneration, c.nextCheckAt
FROM c
WHERE c.kind = "Lifecycle" AND c.nextCheckAt <= @now
  AND (c.nextCheckAt > @afterDueAt
    OR (c.nextCheckAt = @afterDueAt AND c.listId > @afterListId)
    OR (c.nextCheckAt = @afterDueAt AND c.listId = @afterListId
      AND c.id > @afterId))
ORDER BY c.nextCheckAt ASC, c.listId ASC, c.id ASC

-- recovery container, scoped to PartitionKey(listId);
-- cleanup keyset (channelId, id), with @take <= 25
SELECT TOP @take c.id, c.channelId, c.generation, c.state, c.leaseUntil
FROM c
WHERE c.kind = "Edge" AND c.active = true
  AND (c.channelId > @afterChannelId
    OR (c.channelId = @afterChannelId AND c.id > @afterId))
ORDER BY c.channelId ASC, c.id ASC

-- final verification uses the same partition and all active states
SELECT TOP 1 c.id
FROM c
WHERE c.kind = "Edge" AND c.active = true
ORDER BY c.channelId ASC, c.id ASC
```

The implementation may express a lexicographic predicate as equivalent
parameterized query branches if required by the SDK query planner; it may not
drop the total order. Emulator validation showed that Cosmos requires a
composite matching the actual `ORDER BY` tuple; equality-filter paths prepended
to a composite do not satisfy that order. Keep the filter-leading composites
for selective filtering/measurement, and add these query-order composites:

- lists:
  `/membershipRecoveryDueAt`, `/id`;
- channels:
  `/projectionRecoveryDueAt`, `/id`;
- recovery edges:
  `/nextAttemptAt`, `/listId`, `/id`;
- recovery lifecycles:
  `/nextCheckAt`, `/listId`, `/id`; and
- partition-scoped lifecycle cleanup:
  `/channelId`, `/id`.

Every work kind has a durable global cursor containing `cycleNow`, the last full
keyset tuple, and `cycleGeneration`. The cursor advances after each examined
page, even when claims lose races. When no row exists after the key, the worker
wraps atomically by clearing the key, incrementing the cycle generation, and
choosing a new `cycleNow`; it does not repeatedly restart early within one
cycle. Work inserted behind the key is therefore picked up on the next wrap,
while continuous early inserts cannot starve later keys. If a stored SDK
continuation is invalid or too large, the same keyset resumes the cycle.
Task 2110 must capture emulator query metrics/plans proving these queries use the
intended composite indexes rather than enabling scans.

The four kinds also share a fifth durable
`cursor:work-kind-rotation` record with order
`Membership -> Projection -> EdgeDue -> LifecycleDue -> Membership`. Before
admitting any page, an instance conditionally reads `nextStartingKind`, returns
that kind as a page ticket, advances the cursor to its successor, and increments
`rotationGeneration`. The ticket write is durable before the query starts. A
pass obtains one ticket/page at a time and checks its item/RU budget before
requesting another. Thus a membership page that consumes the remaining RU makes
the next pass start at Projection; subsequent tickets reach EdgeDue (including
due poison records) and LifecycleDue even while membership is continuously
replenished.

If an instance crashes after advancing the rotation cursor but before or during
the page, that one ticket may do no useful work, but the successor remains
durable; after at most four further admitted tickets the skipped kind is offered
again. Multiple instances serialize ticket admission with ETags. After the
second ticket conflict an instance performs no un-ticketed query and reports
more work so the worker retries later. Cross-kind fairness therefore composes
with each kind's fixed-cycle keyset fairness: continuous early membership cannot
starve lifecycle work, and continuous non-poison edges cannot starve a later
poison edge.

### Failure Matrix

In the table, "due" means durable work remains discoverable by an indexed pending
or deadline query. Every target write uses an initial ETag attempt plus one
reread/retry; exhaustion leaves the work due.

| Flow and last durable side effect | State after interruption | Expected convergence |
| --- | --- | --- |
| Add: none | No membership committed | Retry is safe; there is nothing to recover. |
| Add: lifecycle/edge candidate batch written | List is unchanged; lifecycle count/generation includes the candidate, which is due after its owner lease | Recovery reads the list, removes any provisional reverse reference, and transactionally retires/counts down the candidate. A 126th active edge is rejected without a partial write. |
| Add: provisional channel reference written | Channel can temporarily contain a dead reference and TTL is disabled | The candidate remains durable. Recovery removes it if the list write never committed. |
| Add: list membership/version/pending written | Membership is successful and the channel already has the reference; edge and list flag both remain evidence | Recovery rereads the list, normalizes count/orphan/TTL, tracks the edge, and clears the matching pending version. |
| Add: edge tracked or channel normalized | Membership is correct but the list pending flag may remain | Reprocessing is idempotent and clears only the observed version. |
| Remove: edge ensured, before list write | Membership is unchanged | Recovery reads the list and keeps/normalizes the reference and tracked edge. |
| Remove: list membership/version/pending written | List no longer contains the channel; reverse reference may remain | Recovery removes only this list id, recalculates count/orphan/TTL, and retires the edge. |
| Remove: channel repaired | Edge or list pending flag may remain | Reprocessing confirms absence, then transactional retirement decrements the lifecycle count/generation exactly once and clears only the matching list version. |
| Membership traversal: this worker retires a candidate | Its transactional batch advances lifecycle edge generation by the exact expected value | The batch atomically adopts that returned generation with the keyset and continues; it does not restart for its own change. |
| Membership traversal: another worker retires/creates/reactivates an edge | Lifecycle generation differs from the traversal's expected value | The membership keyset is cleared and traversal restarts from the beginning, so an externally reordered edge cannot be skipped. |
| Explicit delete: lifecycle marked `Deleting` | List still exists | Recovery point-reads it and safely resumes seeding or returns lifecycle state to active. |
| Explicit delete: any proper subset of edge records seeded | List still exists because deletion is not yet allowed | Retry seeds the remaining bounded set; no membership evidence is lost. |
| Explicit delete: list delete committed | Lifecycle plus all list edges survive | Recovery treats 404 as authoritative absence and repairs every channel page. |
| Explicit delete: any subset of channels repaired/edges retired | Remaining edges, active count/generation, and keyset checkpoint are durable | A new instance resumes with the expected generation from committed retirements; only an external mismatch restarts. Repeated repaired edges are harmless. |
| Explicit/TTL delete: last page looked empty | A leased, poison, or newly inserted edge may still exist, or count/generation may have changed | Completion performs a from-start all-state active-edge query, requires count zero, and conditionally completes the lifecycle ETag/generation; otherwise cleanup remains due. |
| TTL: list physically deleted | Lifecycle and tracked edges survive in another container | The due lifecycle check observes 404 and starts bounded edge cleanup. |
| TTL: any subset of channels repaired/edges retired | Unprocessed edges remain; processed zero-count channels have orphan TTL | Recovery resumes. Eventually no deleted-list reference can keep any channel, including an unavailable one, non-orphaned. |
| Projection: canonical channel/version/pending written | Canonical data is durable; zero or more list projections are stale | Indexed projection-pending work fans out again from the current channel and binds progress to projection and subscription generations. |
| Projection: any subset of lists updated/keyset advanced | Some projections are current; dead references may have been discovered | Retry resumes strictly after the last list id only while both generations match; a reference mutation clears the key and restarts. |
| Projection: reverse set changed after processing `A` in `[A,B]` | `subscriptionGeneration` changes; an old integer offset would skip `B` | The generation mismatch invalidates `afterListId`, restarts current references from the beginning, and processes `B`. |
| Projection: dead edge activated or pending cleared | Membership recovery may remain, or the observed projection/reference generations are complete | Edge recovery uses list truth; conditional clear cannot erase a newer refresh or reference-set change. |
| Renewal: list TTL renewed, lifecycle update failed | Lifecycle may become due too early | Its point read finds the list and reschedules from the authoritative expiry. |
| Cross-kind ticket: RU/item budget exhausted by Membership | Rotation already points to Projection | The next pass begins at Projection and later tickets reach EdgeDue/poison and LifecycleDue despite continuous membership work. |
| Cross-kind ticket: process stops after durable ticket advance | The ticket's page may not run, but its successor is persisted | The next admitted ticket uses the successor; the skipped kind recurs within one four-kind rotation. |
| Any flow: process stops after a claim/checkpoint/cursor write | A lease may hide work temporarily; the global cursor may be mid-cycle | A fresh instance resumes after lease expiry from durable flags and total-order keysets; wrap semantics eventually revisit work inserted behind the cursor. |

### Convergence Argument

Assume Cosmos eventually accepts requests, a recovery instance continues to run,
and no mutation continues after losing its bounded lease. If an add commits list
membership, the provisional channel write has already succeeded and both an edge
and the list's pending version are durable. If the reference is later disturbed
by a competing write, ETag protection forces that writer to reread, while the
pending/edge path still reasserts current list truth. Thus a successful
membership cannot remain permanently missing from canonical channel state.

For remove or deletion, the edge survives until a list point read proves absence
and the corresponding channel update succeeds. ETag conflicts do not discard the
edge. Each successful repair strictly removes the dead list id, recomputes the
count, and applies orphan TTL when the set becomes empty. Therefore a deleted or
TTL-expired list cannot keep any channel alive indefinitely. Concurrent add,
remove, renewal, deletion, or projection work changes an ETag/version; stale
workers fail their conditional completion and retry from current list truth.
Lifecycle completion cannot race past unseen work because every active-set
change increments the transactionally maintained count/generation, and final
completion requires both count zero and a from-start query that includes leased
and poison states. Projection completion similarly requires unchanged canonical
and subscription generations.

Fair scheduling is durable at both levels. A ticket advances the starting kind
before a page can consume the shared budget, so every four admitted tickets
offer every kind regardless of membership backlog. Within EdgeDue, its persisted
total-order keyset must wrap before restarting, so due poison records cannot be
starved by newly inserted earlier edges. Under the existing fair-worker/eventual
Cosmos assumptions, lifecycle and poison recovery therefore continue to make
progress.

### Bounded Work, Multi-Instance Safety, And Observability

Recovery has the four exact indexed due queries above. Each query uses
`MaxItemCount = 25`; a pass processes at most 100 items. It also stops scheduling
new items after 2,000 measured RU and carries the total-order keyset/checkpoint to
the next pass. Per-kind cursor records rotate the starting position after
restart so a continuously replenished early range cannot starve later work; the
shared work-kind ticket cursor supplies one page at a time in durable
round-robin order. The RU limit can exceed by at most the currently executing
item, including its one conflict retry. List membership is bounded by 100 and
all active edges, including failed candidates, by 125. Projection fan-out is
keyset-paged and bounded by the channel document's serialized-size ceiling. The
16-KiB recovery-document and 1.9-MiB list/channel ceilings bound individual
request cost.

Instances claim records with ETag-protected owner/lease fields. Claims, work, and
completion are at-least-once; no correctness depends on exclusive execution.
An expired lease is reclaimable after restart. A list/channel ETag or recovery
generation mismatch makes the worker abandon completion and leave the item due.

Transient failures use exponential backoff with jitter from one minute through
one hour. After ten failed attempts, an item is marked `Poison`, logged at error
level, and retried once per day rather than discarded or TTL-deleted. Thus poison
state reduces load but does not break eventual recovery. Malformed records remain
quarantined for operator repair and never cause a broad scan.

Structured logs and metrics include work kind/id, list/channel ids (never list
tokens), observed version, attempt, lease owner, request charge, duration, result,
next attempt, and sanitized error class. Required aggregates are pending and
poison counts, claimed/retried/succeeded repairs, ETag conflicts, list 404s,
channels orphaned, convergence latency, per-pass RU/items, lease steals, and the
oldest pending age. Alerts cover poison count, oldest age above the lifecycle
SLO, repeated RU-budget exhaustion, and a lifecycle record overdue after list
expiry.

## Worker Logging

The unified worker should log one summary per pass:

- whether purge ran
- number of stale channel ids discovered
- number of channels selected for the batch
- number of YouTube metadata calls
- number of playlist calls
- number of duration batch calls
- number of channels refreshed
- number of channels marked unavailable
- number of projection updates attempted
- number of projection updates succeeded
- next channel refresh time
- next purge time

Cancellation logs should distinguish:

- cancellation before starting YouTube work
- cancellation during YouTube work, followed by persistence finalization
- cancellation during sleep

## Implementation Order Recommendation

After Task 001a and Task 001b, implement channel status before URL lookup cache and list read models. Channel status is the highest-uncertainty early task because it touches YouTube assumptions and status propagation. Then refactor list read models around the status-aware domain shape. Daily authenticated renewal can follow because it is more isolated.
