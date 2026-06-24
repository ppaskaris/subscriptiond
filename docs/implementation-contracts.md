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
    Task<IReadOnlyList<StaleChannelReference>> ClaimStaleBatchAsync(
        DateTimeOffset now,
        DateTimeOffset visibleAfter,
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

`ClaimStaleBatchAsync` is the provider-specific coordination point before YouTube work begins. SQL advances `VisibleAfter` while selecting the batch; later providers should use their own lease or optimistic coordination mechanism. `GetNextActiveSubscribedRefreshAtAsync` returns the next effective refresh time for active subscribed channels, or `null` when no active subscribed channel work is known.

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
public interface IWorkerStateStore
{
    Task<WorkerState> GetOrCreateAsync(CancellationToken cancellationToken);
    Task ForceChannelRefreshAsync(CancellationToken cancellationToken);
    Task CompleteChannelRefreshPassAsync(
        DateTimeOffset? observedNextChannelRefreshAt,
        long observedChannelRefreshForceCount,
        DateTimeOffset? nextChannelRefreshAt,
        CancellationToken cancellationToken);
    Task CompletePurgeAsync(DateTimeOffset nextPurgeAt, CancellationToken cancellationToken);
}
```

`ForceChannelRefreshAsync` sets `NextChannelRefreshAt = DateTimeOffset.MinValue`.

`CompleteChannelRefreshPassAsync` must not overwrite a forced refresh that happened during the worker pass. Providers should compare both the observed channel refresh time and an observed force generation/counter so a second force is not erased when the pass itself observed the forced sentinel value.

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
- share-link consume updates
- worker state channel completion when protected by observed state

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

ChannelLookupCacheDuration = 24 hours
ChannelLookupCacheSizeLimit = 1000

ChannelUnavailableStaleDelay = 100 years
ChannelOrphanRetention = 7 days
```

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
