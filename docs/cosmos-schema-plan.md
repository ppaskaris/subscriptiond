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

Projected channel videos are capped by rule:

```text
all videos published within the last 5 days
plus older videos until at least max(5, ceil(100 / channelCount * 1.33))
```

The list page flattens embedded projected channel videos in memory, sorts newest first, and renders 100.

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
