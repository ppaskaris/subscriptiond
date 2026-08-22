# Cosmos Operations

## Supported Deployment

The supported deployment is intentionally narrow:

- one Azure Cosmos DB for NoSQL account;
- lifetime free tier selected when the account is created;
- one region;
- one database with 1,000 RU/s manual shared throughput;
- the three containers defined in [`cosmos-data-model.md`](cosmos-data-model.md);
- one application instance;
- a Cosmos connection string or endpoint/key stored in Azure App Service configuration, never in
  source control;
- local, manual validation and deployment using the repository scripts.

Serverless is not the free-tier target. Dedicated throughput on three containers is not the
free-tier target. Production startup must detect and explain an unexpected throughput or container
shape instead of silently accepting it.

Managed identity, private networking, multiple regions, distributed worker leases, hosted CI, and
automatic deployment are outside the current hobby deployment. They may be reconsidered if the
service becomes public infrastructure rather than a personal test server.

## Provisioning Checklist

1. Confirm that the Azure subscription has no other account consuming its one free-tier discount.
2. Create the Cosmos account with free tier enabled.
3. Create the database with exactly 1,000 RU/s manual shared throughput.
4. Configure the Cosmos connection string only in the intended staging slot or test application.
5. Allow the application initializer to create missing containers, or create them manually from
   the documented policies.
6. Restart and verify that startup validates throughput, partition keys, TTL, and indexing.
7. Run an authenticated list flow and inspect request counts, RU, throttling, and secret-safe logs.

Record the account, database, region, throughput mode, backup mode, and container policy in the
operator notes without recording credentials.

Use [`cosmos-release-validation.md`](cosmos-release-validation.md) for the checked-in emulator
envelope, read-only Azure resource-shape check, smoke-test flow, and evidence format.

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
