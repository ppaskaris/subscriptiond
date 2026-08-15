# SQL-To-Cosmos Migration Rehearsal And Cutover Checklist

This is the operator checklist for a stopped-site SQL-to-Cosmos migration. Run it first against
synthetic LocalDB data and a disposable emulator database. Running it against the test server,
changing Azure configuration, stopping or starting the app, deploying, or opening traffic requires
explicit authorization. Never reuse a rehearsal target for cutover.

SQL remains authoritative until reconciliation and every pre-open smoke check pass. Rollback is
lossless only before Cosmos accepts public writes. After public traffic opens, do not switch back to
the frozen SQL database without a separately reviewed Cosmos-to-SQL delta plan.

## Evidence And Secret Handling

Create an ignored evidence directory and keep credentials only in process environment variables:

```powershell
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$evidence = ".local/migration/$runId"
New-Item -ItemType Directory -Path $evidence | Out-Null

$env:SUBSCRIPTIOND_MIGRATION_SQL = "<source SQL connection string>"
$env:SUBSCRIPTIOND_MIGRATION_COSMOS = "<target Cosmos connection string>"
$env:SUBSCRIPTIOND_MIGRATION_DATABASE = "<fresh target database name>"
```

Do not use `Start-Transcript`. Do not redirect commands that can echo configuration. Migration
output is safe to capture: it contains counts, an opaque reconciliation hash, total and target
initialization duration, post-initialization target SDK operation count/RU charge, and surfaced 429
count. Target operation/RU values deliberately exclude the read-only database/container
initialization checks; `InitializationIncludedInTargetMetrics=false` makes that boundary explicit.
It does not contain tokens, share passwords, connection
strings, document bodies, personal metadata, or raw Cosmos diagnostics. Azure Metrics is the
authority for throttles that the SDK handled internally.

Record only sanitized values in `evidence.json`, using
[`migration-rehearsal-evidence.example.json`](migration-rehearsal-evidence.example.json) as the
field list. Do not copy the example into a tracked path after adding real values.

## Release Prechecks

Run sequentially from the repository root. Stop at the first failure.

```powershell
dotnet build youtubed.sln

dotnet test youtubed.sln --no-build `
    --filter "Category!=LocalDb&Category!=Cosmos"

$env:YOUTUBED_RUN_LOCALDB_TESTS = "true"
dotnet test youtubed.sln --no-build --filter "Category=LocalDb"

$env:YOUTUBED_RUN_COSMOS_TESTS = "true"
dotnet test youtubed.sln --no-build --filter "Category=Cosmos"

dotnet format youtubed.sln --verify-no-changes --no-restore
git diff --check
dotnet list youtubed.sln package --vulnerable --include-transitive
```

Safe output means every selected suite ran and passed, formatting and whitespace checks return zero,
and the vulnerability scan reports no vulnerable direct or transitive package. A skipped provider
suite is not a pass.

Capture the focused synthetic rehearsal separately:

```powershell
$env:YOUTUBED_RUN_LOCALDB_TESTS = "true"
$env:YOUTUBED_RUN_COSMOS_TESTS = "true"
dotnet test youtubed.sln --no-build `
    --filter "FullyQualifiedName~SqlToCosmosImportIntegrationTests.Import_RecoversAfterDurableInterruptionAndMatchesProviderBehavior" `
    --logger "console;verbosity=detailed" 2>&1 |
    Tee-Object "$evidence/emulator-rehearsal.txt"
if ($LASTEXITCODE -ne 0) { throw "Migration rehearsal failed." }
```

Expected safe evidence contains a `StoppedSite` line, separate `MigrationRehearsal Rehearsal=1`
and `Rehearsal=2` lines, a complete `PreOpenSmoke` line, and separate smoke and reconciliation
`FailureInjection` lines. It records the 76-minute simulated drain, stopped-write timestamp,
downtime, three interrupted reruns in rehearsal 1, per-rehearsal post-initialization operation/RU
metrics, zero surfaced throttles, both opaque hashes, configured Cosmos and SQL provider names, and
successful rollback. The test uses generated IDs, tokens, titles, URLs, channels, and videos; it
writes no real metadata. Both reconciled targets must report the same hash.

## Share-Link Drain

The maximum generated share-link lifetime is 75 minutes. Disable only creation while leaving
existing link consumption and deletion available:

```powershell
az webapp config appsettings set `
    --resource-group "<resource-group>" `
    --name "<app-name>" `
    --settings ShareLinks__CreationEnabled=false `
    --output none
az webapp restart --resource-group "<resource-group>" --name "<app-name>" --output none
```

Expected behavior: the authenticated share page says new links are temporarily unavailable, its
create control is absent, an authenticated POST to the unchanged `share/create` route returns 503,
and existing share URLs still resolve or can be deleted. Record the disable timestamp. Wait at
least 76 minutes, then run this read-only query against SQL:

```sql
SELECT COUNT_BIG(*) AS ValidUnconsumedShareLinks
FROM ShareLink
WHERE UsedAt IS NULL
  AND ExpiresAfter > SYSUTCDATETIME();
```

Continue only when the result is `0`. Do not delete links to manufacture a successful drain.

## Close Writes And Import

Confirm the reviewed artifact, fresh correctly provisioned target, SQL backup/retention decision,
rollback owner, and a private smoke-test path. Then close public traffic and stop every writer. Do
not continue if SQL can still change. For App Service, the authorized stop command is:

```powershell
az webapp stop --resource-group "<resource-group>" --name "<app-name>" --output none
```

Run the read-only source validation:

```powershell
dotnet run --no-build --project youtubed -- import-sql-to-cosmos validate `
    --SourceConnectionString $env:SUBSCRIPTIOND_MIGRATION_SQL `
    --TargetConnectionString $env:SUBSCRIPTIOND_MIGRATION_COSMOS `
    --TargetDatabaseName $env:SUBSCRIPTIOND_MIGRATION_DATABASE `
    --BatchSize 100 2>&1 | Tee-Object "$evidence/validate.txt"
if ($LASTEXITCODE -ne 0) { throw "Validation failed; keep SQL selected." }
```

For the first import, require the verified-empty confirmation:

```powershell
dotnet run --no-build --project youtubed -- import-sql-to-cosmos import `
    --SourceConnectionString $env:SUBSCRIPTIOND_MIGRATION_SQL `
    --TargetConnectionString $env:SUBSCRIPTIOND_MIGRATION_COSMOS `
    --TargetDatabaseName $env:SUBSCRIPTIOND_MIGRATION_DATABASE `
    --BatchSize 100 `
    --confirm-empty-target 2>&1 | Tee-Object "$evidence/import.txt"
if ($LASTEXITCODE -ne 0) { throw "Import failed; traffic remains closed." }
```

During one rehearsal, press Ctrl+C after at least one target write. The command must report
cancelled. Without opening traffic or changing either data source, rerun with:

```powershell
dotnet run --no-build --project youtubed -- import-sql-to-cosmos import `
    --SourceConnectionString $env:SUBSCRIPTIOND_MIGRATION_SQL `
    --TargetConnectionString $env:SUBSCRIPTIOND_MIGRATION_COSMOS `
    --TargetDatabaseName $env:SUBSCRIPTIOND_MIGRATION_DATABASE `
    --BatchSize 100 `
    --confirm-pre-cutover-rerun 2>&1 | Tee-Object "$evidence/import-rerun.txt"
if ($LASTEXITCODE -ne 0) { throw "Idempotent rerun failed; discard the target." }
```

Reconcile without target writes:

```powershell
dotnet run --no-build --project youtubed -- import-sql-to-cosmos reconcile `
    --SourceConnectionString $env:SUBSCRIPTIOND_MIGRATION_SQL `
    --TargetConnectionString $env:SUBSCRIPTIOND_MIGRATION_COSMOS `
    --TargetDatabaseName $env:SUBSCRIPTIOND_MIGRATION_DATABASE `
    --BatchSize 100 2>&1 | Tee-Object "$evidence/reconcile.txt"
if ($LASTEXITCODE -ne 0) { throw "Reconciliation failed; roll back before opening traffic." }
```

Record validate/import/reconcile total and initialization duration, total downtime so far, each
mode's post-initialization target SDK operation/RU values, surfaced throttles, and Azure
`TotalRequests`, `TotalRequestUnits`, and 429 metrics for the same UTC interval. The CLI target
operation/RU values exclude initialization and are not a substitute for the Azure interval totals.
Counts and reconciliation hashes must agree. Any mismatch, target share link,
unexpected target document, or unexplained throttle is a stop decision; never weaken reconciliation.

## Private Pre-Open Smoke

Select Cosmos only on the private staging slot or otherwise access-restricted app, set the target
secret there, deploy the reviewed artifact, and start it without opening public traffic. These are
Azure mutations and require explicit authorization. Startup must validate the exact shared-throughput
database and three-container policies.

Use only synthetic smoke records. Verify, in order:

1. A known imported anonymous list URL authenticates; never record its token or full URL.
2. Membership, title, playback rate, channel active/unavailable status, newest videos, absolute
   expiry, TTL, and once-daily renewal match SQL.
3. Viewing a stale imported channel queues and completes request-driven refresh.
4. Channel add, remove, cache-hit re-add, force refresh, and video rendering succeed.
5. Share creation, listing, one-time consumption, rejected reuse, and deletion succeed after
   temporarily enabling creation only on the closed smoke host.
6. A synthetic list can be deleted and its anonymous URL no longer resolves.
7. Record smoke duration, request count, RU, 429/retry observations, and queue behavior. Scan the
   evidence for tokens, passwords, keys, connection strings, bodies, diagnostics, and real metadata.

The request/RU envelope and exact structured logging rules are in
[`cosmos-release-validation.md`](cosmos-release-validation.md).

## Failure Injection And Rollback Decision

On a disposable rehearsal target, change one synthetic list title after import and require
`reconcile` to fail with a sanitized list-mismatch message. Restore by discarding the target, not by
weakening reconciliation. Separately, inject a failure into a required private-host smoke operation
so that the configured Cosmos host returns a failed smoke result; ordinary wrong-token rejection is
an authentication success criterion, not a smoke injection. The automated rehearsal replaces the
correct-token list-page result only in the test host, requires the known URL to fail instead of
returning HTTP 200, stops that host, starts a separately configured SQL host, and requires the known
SQL-backed list URL to return HTTP 200. The reconciliation-mismatch branch independently starts its
own SQL-configured host and requires the same URL to return HTTP 200; it does not rely on a direct
repository read or the smoke-failure branch's later host. In both cases:

1. keep public traffic closed;
2. stop the Cosmos-configured host;
3. restore `Persistence__Provider=SqlServer` and the unchanged SQL connection configuration;
4. start the private host and require the same known SQL-backed smoke checks to pass;
5. record `Decision=Rollback`, the failure category, timestamps, and the sanitized evidence path.

Do not copy data back from Cosmos. Do not reuse the failed target.

## Open Or Abort

Open traffic only when all prechecks, drain, import, reconciliation, and smoke checks pass and the
authorized owner explicitly accepts the rollback boundary. Record `Decision=OpenCosmos`, the UTC
open time, artifact version, evidence paths, retained SQL backup/location identifier, and retention
deadline without recording credentials or anonymous URLs.

If any check fails, restore SQL while traffic is still closed and record `Decision=Rollback`. If
Cosmos has accepted public writes, stop: direct SQL rollback is no longer lossless and requires a
separately reviewed delta procedure.
