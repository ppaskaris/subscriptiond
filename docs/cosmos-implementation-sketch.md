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
    "SystemContainer": "system"
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

Do not expose Cosmos DTOs from repository interfaces.

## List Repository

Authenticated list access:

1. point-read list document
2. compare route token with stored raw token using constant-time comparison
3. if authenticated and `expirationRenewedOn != clock.UtcToday`, patch:
   - `expiredAfter`
   - `expirationRenewedOn`
   - `ttl`
4. return the requested domain read model

Read-model reads:

- point-read list document
- map embedded channels to `ListChannelProjection` for channel management
- map embedded channels with nested videos to hierarchical `ListVideoProjection` for the main list page
- do not renew expiration

Add channel:

- point-read list document with ETag
- append embedded channel projection if missing
- replace or patch with ETag
- retry on conflict

Remove channel:

- point-read list document with ETag
- remove embedded channel projection by id
- replace or patch with ETag
- retry on conflict

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

- validate `subscribedListIds` when projecting or repairing
- remove missing/inconsistent list ids
- set `subscriptionCount = subscribedListIds.Count`
- if count is zero, set orphan TTL

## List Projection Repository

SQL implementation is no-op.

Cosmos implementation:

1. For each refreshed channel, build a projected channel subdocument.
2. For each subscribed list id, point-read the list.
3. If missing or the list no longer contains the channel, collect a dead reference for channel repair.
4. If the list contains the channel, replace that channel subdocument in memory.
5. Write the list document with ETag.
6. On conflict, re-read and reapply.

Projection updates should only touch channels processed in the current batch. Do not point-read every other channel in the list just to rebuild a perfect projection.

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

## Expiration Purger

Cosmos implementation is no-op. TTL handles physical deletion.

The worker still calls the purger through the shared interface so SQL and Cosmos share the same worker flow.

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
