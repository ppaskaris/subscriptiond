# Migration And Operations

## Supported Deployment

The supported Cosmos deployment is intentionally narrow:

- one Azure Cosmos DB for NoSQL account;
- lifetime free tier selected when the account is created;
- one region;
- one database with 1,000 RU/s manual shared throughput;
- the three containers defined in [`cosmos-data-model.md`](cosmos-data-model.md);
- one application instance;
- a Cosmos connection string or endpoint/key stored in Azure App Service configuration, never in
  source control;
- local, manual validation and deployment using the repository scripts and runbook.

Serverless is not the free-tier target. Dedicated throughput on three containers is not the
free-tier target. Production startup must detect and explain an unexpected throughput or container
shape instead of silently accepting it.

Managed identity, private networking, multiple regions, distributed worker leases, hosted CI, and
automatic deployment are outside the current hobby deployment. They may be reconsidered if the
service becomes public infrastructure rather than a personal test server.

## Provisioning Checklist

Provisioning is a deliberate manual operation:

1. Confirm that the Azure subscription has no other account consuming its one free-tier discount.
2. Create the Cosmos account with free tier enabled.
3. Create the database with exactly 1,000 RU/s manual shared throughput.
4. Configure the application secret and select `Persistence:Provider=Cosmos` only in the intended
   staging slot or test application.
5. Allow the application initializer to create missing containers, or create them manually from
   the documented policies.
6. Restart and verify that startup validates throughput, partition keys, TTL, and indexing.
7. Run an authenticated list flow and inspect request counts, RU, throttling, and secret-safe logs.

Record the account, database, region, throughput mode, backup mode, and container policy in the
operator notes without recording credentials.

Use [`cosmos-release-validation.md`](cosmos-release-validation.md) for the checked-in emulator
envelope, read-only Azure resource-shape check, smoke-test flow, and evidence format.

## Migration Strategy

Use a bounded-downtime offline import. Do not implement dual writes.

The source SQL database remains authoritative until the Cosmos import and smoke test pass. The
target is a fresh empty Cosmos database. Deterministic IDs and upserts make a repeated import
idempotent, but rerunning against a target that has accepted user writes is prohibited.

### Share-link drain

Share links are intentionally not migrated. Before the migration window, disable new share-link
creation and wait longer than the configured maximum share-link lifetime. Confirm that no valid
unconsumed links remain. This avoids moving short-lived passwords and consumption races while
preserving permanent anonymous list URLs.

### Data mapping

Import:

- each non-expired SQL list as one list document, preserving ID, token bytes, settings, absolute
  expiry, renewal date, and its sorted distinct `ListChannel` channel IDs;
- each channel referenced by an imported list as one channel document, preserving canonical
  metadata, availability status, stale timestamp, playlist ID, and its newest 100 videos;
- no share-link documents;
- no SQL worker state;
- no reverse references, projections, recovery records, orphan state, or migration checkpoints in
  application containers.

Skip expired lists and channels referenced only by skipped lists. Compute TTL from the original
absolute expiry at write time; never extend a list merely because migration was rerun.

### Import command

The migration command supports only the modes needed for this offline operation:

- `validate`: read the source, calculate mappings and counts, serialize every target shape, and
  perform no Cosmos writes;
- `import`: upsert the deterministic target documents into a confirmed empty migration target;
- `reconcile`: compare secret-safe counts, IDs, membership, expiry, status, and representative
  domain-visible reads.

It reads SQL in bounded batches, respects Cosmos 429 retry guidance, and never prints tokens,
share passwords, connection strings, document bodies, or raw SDK diagnostics. A failed import can
be rerun before cutover; for ambiguous target state, discard the target database and start again.
A durable general-purpose checkpoint/poison framework is not part of the application.

## Cutover Runbook

1. Build and run ordinary tests, opted-in LocalDB tests, and opted-in Cosmos emulator tests in the
   required sequential order.
2. Run format, diff, and package-vulnerability checks required by `AGENTS.md`.
3. Confirm the production Cosmos database is empty and correctly provisioned.
4. Disable share creation and complete the share-link drain.
5. Stop the web application so SQL cannot change.
6. Run migration `validate`, `import`, and `reconcile` against the stopped SQL source.
7. Configure the application for Cosmos and deploy without reopening public traffic.
8. Smoke-test known list URLs, list renewal, channel management, add/remove, forced refresh,
   share creation/consumption, and deletion using non-sensitive test records.
9. If smoke or reconciliation fails, switch back to SQL before reopening traffic.
10. If they pass, reopen traffic and record the cutover time and evidence.

Keep the SQL database unchanged for an agreed retention period.

## Rollback Boundary

Rollback to SQL is lossless only while traffic remains stopped because the design has no dual
writes. Once Cosmos accepts user mutations, switching directly back to the frozen SQL database can
lose those mutations. After traffic opens, prefer a forward fix; any later rollback requires a
separately reviewed Cosmos-to-SQL delta procedure.

This boundary must be stated in the cutover checklist and accepted before traffic is reopened.

## Operational Checks

For the hobby deployment, retain a small set of actionable signals:

- Cosmos request count, RU charge, latency, status/substatus, and retry count;
- repeated 429, timeout, or service-unavailable failures;
- failed list authentication without logging IDs or tokens at normal levels;
- channel refresh success/failure and queue depth;
- total storage and normalized RU consumption from Azure metrics;
- startup failure for throughput or container-policy drift.

Do not parse or export raw Cosmos diagnostics if they can contain resource identifiers. Do not
restore the previous recovery-specific metrics, SLOs, cursors, or poison alerts.
