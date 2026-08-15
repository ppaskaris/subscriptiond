# Simplified Cosmos Data Model

## Database And Throughput

Use one Azure Cosmos DB for NoSQL account with the lifetime free-tier discount enabled at account
creation. Provision one database with 1,000 RU/s manual shared throughput. All three containers
inherit that database throughput; no container receives dedicated throughput.

The application may create missing containers during development. In production it must verify
that the database already has the expected shared throughput and that existing containers have
the expected partition key, TTL setting, and indexing policy. It must not silently create a
different billing shape.

The Azure CLI indexing-policy artifacts are checked in under [`youtubed/CosmosSchema/`](../youtubed/CosmosSchema/):
`lists.index.json`, `channels.index.json`, and `shareLinks.index.json`. Pass the matching file to
`az cosmosdb sql container create --idx`; keep the partition key and TTL values described below.

## Containers

The provider uses exactly three containers:

- `lists`, partitioned by `/id`;
- `channels`, partitioned by `/id`;
- `shareLinks`, partitioned by `/id`.

There is no `system` or `recovery` container.

## Lists

A list document owns identity, authentication, settings, lifecycle, and membership:

```json
{
  "id": "list-guid",
  "token": "opaque-encoded-secret-bytes",
  "title": "My list",
  "playbackRate": 1.0,
  "expiredAfter": "2026-09-29T12:00:00Z",
  "expirationRenewedOn": "2026-08-14",
  "channelIds": ["UC...", "UC..."],
  "ttl": 3974400
}
```

Rules:

- `channelIds` is the complete and only membership authority.
- IDs are unique and stored in ordinal order for deterministic serialization.
- Membership and settings writes replace or patch this one document with an ETag.
- One conflict is reread and reapplied; a second conflict is returned as a visible failure.
- TTL is recomputed from the unchanged absolute `expiredAfter` deadline whenever the document is
  written. Renewal changes that absolute deadline at most once per UTC day.
- The serialized token is secret data and is never included in logs, metrics, URLs other than the
  existing user-facing secret route, migration output, or exception text.
- Reject an add that would exceed 100 channel IDs.

Indexing excludes `/token/?` and any future large or secret payload. No composite indexes are
required. List access is a point read.

## Channels

A channel document is a reusable cache keyed by canonical YouTube channel ID:

```json
{
  "id": "UC...",
  "url": "https://www.youtube.com/channel/UC...",
  "title": "Channel title",
  "thumbnail": "https://...",
  "playlistId": "UU...",
  "staleAfter": "2026-08-14T13:00:00Z",
  "status": "Active",
  "statusReason": null,
  "statusUpdatedAt": null,
  "videos": [
    {
      "id": "video-id",
      "title": "Video title",
      "durationTicks": 123456789,
      "publishedAt": "2026-08-14T12:00:00Z",
      "thumbnail": "https://..."
    }
  ]
}
```

Rules:

- The document contains no list IDs, subscription count, orphan state, TTL, projection state, or
  recovery state.
- Videos are de-duplicated and bounded to the newest 100 using deterministic timestamp/ID order.
- Completed refreshes replace one channel document with an ETag.
- Missing channel documents are rediscovered from their canonical ID.
- Unavailable status remains cached and visible until a later successful discovery/refresh changes
  it or the user removes the channel ID from a list.
- Unreferenced channel documents are inert. Do not add cleanup until measured storage makes it
  worthwhile; 20 GB is the operational stop-growth threshold for the 25-GB free allowance.

Exclude `/videos/*` from indexing. No due-work index is required because refresh is driven by the
in-memory queue rather than a database scan.

## Share Links

A share-link document is keyed and partitioned by its generated password:

```json
{
  "id": "four-word-password",
  "listId": "list-guid",
  "createdAt": "2026-08-14T12:00:00Z",
  "expiresAfter": "2026-08-14T13:10:00Z",
  "usedAt": null,
  "ttl": 90000
}
```

Rules:

- The document stores only the list ID, never the list token.
- Creation uses create-only semantics so password collisions are retried by the service.
- Consumption is ETag-protected and returns a token only after the used-state write succeeds.
- TTL retains expired or used links only for the existing short diagnostic retention period.
- Listing by `listId` is an intentionally low-volume cross-partition query.

Index only `/listId/?`, `/createdAt/?`, `/expiresAfter/?`, and `/usedAt/?`. Exclude all other paths
unless emulator measurements show a required query cannot use this policy.

## Reads And Request Shape

A list page uses:

1. one `ReadItemAsync` for the list;
2. zero or one `ReadManyItemsAsync` call for at most 100 `(id, partitionKey)` channel pairs;
3. an optional conditional list write for once-daily expiry renewal.

The implementation does not query channels with an `IN` expression and does not read one channel
at a time in an unbounded loop. The channel-management page uses the same bounded list-plus-
channels read without mapping videos into its view model.

## Size And RU Validation

Before enabling Cosmos in production, measure:

- empty, representative, and 100-channel list documents;
- representative and 100-video channel documents;
- same-day and renewal list-page request counts and RU;
- add/remove, channel refresh, share create/consume/list/delete, and TTL writes;
- behavior under an injected 429 followed by SDK retry exhaustion.

Use measurements as regression evidence, not as a reason to build a scheduler or transaction log.
Keep a clear safety margin below 2 MiB and preserve at least 30% of the 1,000 RU/s shared throughput
for retries and concurrent requests. Stop accepting growth if sustained use exceeds 700 RU/s or
storage reaches 20 GB; either reduce the workload or move to paid capacity.

## References

- [Azure Cosmos DB lifetime free tier](https://learn.microsoft.com/azure/cosmos-db/free-tier)
- [Provision throughput for databases and containers](https://learn.microsoft.com/azure/cosmos-db/set-throughput)
- [Optimize request cost](https://learn.microsoft.com/azure/cosmos-db/optimize-cost-reads-writes)
- [Cosmos .NET `ReadManyItemsAsync`](https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.container.readmanyitemsasync)
