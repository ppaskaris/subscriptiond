# Cosmos DB Schema Plan

The Cosmos DB provider optimizes for minimal RU usage on the common list page read.

## Containers

Recommended containers:

- `lists`
- `channels`
- `shareLinks`
- `system`

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
  "orphanedAfter": null,
  "ttl": -1,
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

`subscribedListIds` is the source for reverse references. `subscriptionCount` is an indexed query helper and repair target.

Channel updates must use optimistic concurrency when modifying `subscribedListIds` and `subscriptionCount`.

Unavailable channels remain until user removal and do not refresh.

Orphan channels receive TTL only when `subscriptionCount == 0` and no valid list references remain. Adding a channel to a list clears orphan markers and disables TTL.

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
  "nextPurgeAt": "2026-05-09T13:10:00Z"
}
```

## Indexing

Indexing should be narrowed to reduce write RU.

Lists:

- point reads by id
- exclude `/channels/*`

Channels:

- index `/staleAfter`
- index `/subscriptionCount`
- index `/status` if used in stale queries
- exclude `/videos/*`

ShareLinks:

- support `/listId`
- support `/createdAt`
- support `/expiresAfter`
- support `/usedAt`

System:

- minimal indexing

Exact indexing policy JSON should be written during Cosmos implementation and verified with emulator tests.
