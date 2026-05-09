# Pre-Cosmos Application Behavior Changes

This document describes behavior changes to make before implementing the Cosmos DB backend. These changes should run on SQL Server first so the application shape matches the future Cosmos model.

## List Page

The main list page should render from a `ListVideoProjection` read model:

- list identity/settings
- channel videos with enough channel context for rendering

SQL should build this with a selective query that joins only the fields needed for video rendering. Cosmos can point-read the denormalized list document and reshape embedded channel/video data into the same read model.

The view should sort by `PublishedAt DESC, VideoId ASC` and render at most `Constants.ListRenderMaxItems`.

## Channel Management Page

The channel management page should render from a `ListChannelProjection` read model:

- list identity/settings
- channels in the list

SQL should build this without joining `ChannelVideo`, since the page does not need videos. Cosmos can reshape the embedded channel projection into the same read model.

The exact total video count should be removed from the UI. Instead of:

```text
Showing 100 of N total videos in this list.
```

show count-free copy such as:

```text
Showing the 100 most recent videos in this list.
```

## Stale Channel Banner

`StaleCount` should be kept, but it should be computed from the read model rather than stored as a separate aggregate. The main list page should count stale active channels from channel context in `ListVideoProjection`:

```text
channel.Status == Active && channel.StaleAfter <= view.Now
```

SQL can compute the count directly from joined channel rows. Cosmos can compute the count in memory after point-reading the list document because projected channel summaries include status and stale timestamps.

The banner can keep the current count-based behavior:

```text
3 channels in this list are waiting to be updated.
```

The meta refresh remains only on the main list page. It should use a fixed 15 second interval while any active projected channel is stale.

## Unavailable Channels

Known permanent YouTube failures should become visible channel status, not invisible retry loops.

Channel status:

- `Active`
- `Unavailable`

Status reason:

- `None`
- `NotFound`
- `Deleted`
- `Private`
- `PlaylistUnavailable`

When a permanent failure is detected:

- set status to `Unavailable`
- set status reason
- set status updated timestamp
- set `StaleAfter` far in the future, for example 100 years
- propagate the status into provider projections/read models

Unavailable channels remain in lists until the user removes them. They do not trigger YouTube API calls and do not drive the stale-channel banner.

## List Expiration Renewal

Authenticated list access should renew expiration at most once per UTC day. This applies to authenticated list routes such as index, channels, edit, share, add, and delete flows.

Maintenance and read-model/projection reads must not renew list expiration.

The list document/domain object should include:

- `ExpiredAfter`
- `ExpirationRenewedOn`

When authenticated access succeeds and `ExpirationRenewedOn != clock.UtcToday`, update:

- `ExpiredAfter`
- `ExpirationRenewedOn`

## Channel Lookup

Durable channel URL lookup should stop being part of the domain. YouTube channel id is canonical.

Submitted URLs should be resolved through YouTube, with a bounded in-memory cache to avoid repeated API calls for the same submitted URL.

Suggested cache settings:

- duration: 24 hours
- size limit: 1000 entries

Stored `Channel.Url` is display metadata. Metadata refresh should update it to YouTube's preferred or canonical channel URL when available.

## Projection Rules

SQL can compute projections dynamically with joins. Cosmos will store projections.

The domain read models should describe use cases, not storage shape:

- `ListChannelProjection` supports channel management without reading videos.
- `ListVideoProjection` supports the main list page without reading unneeded channel-management-only data.
- Cosmos denormalization is reshaped in the persistence layer.

Channel video read models include only:

- video id
- channel id
- channel title
- channel URL
- channel status fields needed for banners
- title
- duration
- published timestamp
- thumbnail URL

Channel read models include only fields needed for channel management and membership:

- id
- url
- title
- thumbnail
- stale timestamp
- status fields
- videos
