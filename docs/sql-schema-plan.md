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
ALTER TABLE Channel
ADD Status NVARCHAR(50) NOT NULL CONSTRAINT DF_Channel_Status DEFAULT (N'Active'),
    StatusReason NVARCHAR(100) NULL,
    StatusUpdatedAt DATETIMEOFFSET NULL;
```

Status values map to domain enums:

- `Active`
- `Unavailable`

Reason values map to domain enums:

- `None`
- `NotFound`
- `Deleted`
- `Private`
- `PlaylistUnavailable`

Known permanent YouTube failures should set:

- `Status = 'Unavailable'`
- `StatusReason = reason`
- `StatusUpdatedAt = now`
- `StaleAfter = DateTimeOffset.MaxValue`

## VisibleAfter Removal

When the unified worker replaces SQL lease claiming, remove `VisibleAfter` from SQL.

This removes the old multi-worker claim model. The new worker is a single-worker design that uses `StaleAfter` for both normal refresh scheduling and failure backoff.

Migration should drop `VisibleAfter` only after code no longer reads or writes it.

## WorkerState

Add a unit table:

```sql
CREATE TABLE WorkerState (
    Id INT NOT NULL,
    NextChannelRefreshAt DATETIMEOFFSET NULL,
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
