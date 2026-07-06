# SQL Schema Plan

SQL Server remains the first provider that implements the new domain behavior. The schema should evolve to support the provider-agnostic worker and channel status model, while SQL continues to use relational joins for use-case read models.

## Keep

SQL keeps the normalized tables:

- `List`
- `ShareLink`
- `Channel`
- `ChannelVideo`
- `ListChannel`
- `ChannelVideoType`

SQL computes list channel and list video read models dynamically through selective joins. It does not store embedded list projection documents.

## List Changes

Add daily renewal tracking:

```sql
ALTER TABLE [List]
ADD ExpirationRenewedOn DATE NULL;
```

Authenticated access renews list expiration at most once per UTC day. Maintenance/projection reads do not renew it.

## Channel Changes

Add channel status fields:

```sql
CREATE TABLE ChannelStatus (
    Id INT NOT NULL,
    Name NVARCHAR(50) NOT NULL,

    CONSTRAINT PK_ChannelStatus PRIMARY KEY (Id),
    CONSTRAINT UK_ChannelStatus_Name UNIQUE (Name)
);

CREATE TABLE ChannelStatusReason (
    Id INT NOT NULL,
    Name NVARCHAR(100) NOT NULL,

    CONSTRAINT PK_ChannelStatusReason PRIMARY KEY (Id),
    CONSTRAINT UK_ChannelStatusReason_Name UNIQUE (Name)
);

ALTER TABLE Channel
ADD Status INT NOT NULL CONSTRAINT DF_Channel_Status DEFAULT (0),
    StatusReason INT NOT NULL CONSTRAINT DF_Channel_StatusReason DEFAULT (0),
    StatusUpdatedAt DATETIMEOFFSET NULL;

ALTER TABLE Channel
ADD CONSTRAINT FK_Channel_ChannelStatus
    FOREIGN KEY (Status) REFERENCES ChannelStatus (Id),
    CONSTRAINT FK_Channel_ChannelStatusReason
    FOREIGN KEY (StatusReason) REFERENCES ChannelStatusReason (Id);
```

Status values map to domain enums:

- `0 = Active`
- `1 = Unavailable`

Reason values map to domain enums:

- `0 = None`
- `1 = NotFound`
- `2 = Deleted`
- `3 = Private`
- `4 = PlaylistUnavailable`

The lookup tables provide readable SQL joins while the `Channel` table stores numeric enum values that Dapper can map directly to domain enums.

Known permanent YouTube failures should set:

- `Status = 1`
- `StatusReason = reason enum value`
- `StatusUpdatedAt = now`
- `StaleAfter = DateTimeOffset.MaxValue`

## Stale Channel Selection

SQL no longer stores a channel lease field. The unified worker selects stale work from active subscribed channels ordered by `StaleAfter`, then refreshes the first configured batch. `GetNextActiveSubscribedRefreshAtAsync` uses the earliest active subscribed `StaleAfter` to schedule the next channel pass.

## WorkerState

Add a unit table:

```sql
CREATE TABLE WorkerState (
    Id INT NOT NULL,
    NextChannelRefreshAt DATETIMEOFFSET NULL,
    ChannelRefreshForceCount BIGINT NOT NULL,
    NextPurgeAt DATETIMEOFFSET NOT NULL,

    CONSTRAINT PK_WorkerState PRIMARY KEY (Id),
    CONSTRAINT CK_WorkerState_Id CHECK (Id = 1)
);
```

The worker state repository should get-or-create row `Id = 1`.

## Expiration Purger

SQL implements `IExpirationPurger` with real deletes:

- delete lists whose `ExpiredAfter <= now`
- delete share links whose `ExpiresAfter <= now - retention`
- delete orphan channels whose orphan retention elapsed, if orphan retention is modeled in SQL

Cosmos implements the same interface as no-op because TTL handles physical deletion.

## Projection Writes

SQL implements list projection update ports as no-op. Dynamic joins are the SQL read-model source.

The SQL provider still needs to return the same domain read models as Cosmos so controllers and views can be storage-agnostic.

## Migration Safety

SQL Server migrations must avoid statically referencing a column later in the same batch after conditionally adding it. Use separate batches or dynamic SQL for follow-up updates.

When SQL changes are implemented, run LocalDB integration tests with:

```text
YOUTUBED_RUN_LOCALDB_TESTS=true
```
