# CLI

## Start the Dev Server

Use `scripts/start-dev.ps1` to run the app without Visual Studio and without
locking the normal compiler output under `youtubed/bin` or `youtubed/obj`.

```powershell
.\scripts\start-dev.ps1
```

The script publishes a Debug copy to `artifacts/dev-server/<timestamp>` and runs
that copy in the foreground. Tests can still build and run while the dev server
is up because the running process is not holding files in the project's compiler
output directories.

By default it uses the same URLs as the Visual Studio profile:

- `https://localhost:65503`
- `http://localhost:65504`

Use `Ctrl+C` to stop the server.

## Deploy

Use `scripts/deploy.ps1` to deploy directly to the Azure App Service with Web
Deploy.

In the Azure portal, open the App Service, click **Download publish profile**,
and put the downloaded `.PublishSettings` file under `.local/azure` in this
repository. Files ending in `.PublishSettings` are ignored by git.

```powershell
.\scripts\deploy.ps1
```

The script auto-detects the local publish settings file when exactly one is
available, reads the `publishMethod="MSDeploy"` profile, and uses its Web Deploy
endpoint, app path, user name, and password.

The publish settings file contains the deploy password, so keep it local and do
not paste its contents into commits, issues, logs, or screenshots.

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

## Import SQL Data Into Cosmos

The offline `import-sql-to-cosmos` command has `validate`, `import`, and `reconcile` modes. It is
only for a stopped-site, pre-cutover migration. See
[`docs/sql-to-cosmos-migration-runbook.md`](docs/sql-to-cosmos-migration-runbook.md) for the exact
sequence, confirmation flags, interruption recovery, evidence, and rollback boundary.
