# CLI

## Transfer App Data

The `transfer-data` command copies all subscriptiond app data from one SQL Server
database to another.

It transfers these app tables:

- `Channel`
- `List`
- `ShareLink`
- `ChannelVideo`
- `ListChannel`

The target database must already exist and must already have the app schema
created from `youtubed/Schema.sql` and any required migrations.

### Usage

```powershell
dotnet run --project youtubed -- transfer-data --SourceConnectionString "<source-connection-string>" --TargetConnectionString "<target-connection-string>"
```

### Dry Run

Use `--dry-run` to print the SQL statements that would be run instead of
modifying the target database.

```powershell
dotnet run --project youtubed -- transfer-data --SourceConnectionString "<source-connection-string>" --TargetConnectionString "<target-connection-string>" --dry-run
```

Dry run output is written to stdout and includes:

- A transaction wrapper.
- `DELETE` statements for the target app tables.
- `INSERT` statements for rows read from the source database.

### Behavior

- The command deletes existing app data from the target tables before inserting
  copied rows.
- The live transfer runs inside a target database transaction with
  `XACT_ABORT ON` behavior from the client-side transaction rollback path.
- Data is copied in foreign-key-safe order.
- The command refuses to run when the source and target connection strings point
  to the same SQL database.
- Non-app database objects are not copied.

### Example

```powershell
dotnet run --project youtubed -- transfer-data `
  --SourceConnectionString "Server=old-sql;Database=subscriptiond;Integrated Security=True;TrustServerCertificate=True" `
  --TargetConnectionString "Server=new-sql;Database=subscriptiond;Integrated Security=True;TrustServerCertificate=True"
```

