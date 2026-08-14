# Cosmos Release Validation

## Release Envelope

The opted-in Cosmos integration suite defines three datasets using ordinary application metadata:

| Shape | Channels per list | Videos per channel | Purpose |
| --- | ---: | ---: | --- |
| Small | 1 | 3 | A new personal list |
| Representative | 10 | 20 | The expected hobby workload |
| Maximum | 100 | 100 | Both enforced Cosmos cardinality limits |

`CosmosReleaseEnvelopeIntegrationTests` measures serialized list and the largest generated channel
item plus request count, RU, and latency for same-day rendering, daily renewal, cache-miss add
(channel discovery and membership write), cache-hit add, remove, channel refresh, and all share
operations. Channel IDs use the canonical 24-character YouTube shape. Every item must remain at or
below the 512-KiB safety ceiling, leaving at least
75% headroom below the Cosmos DB for NoSQL 2-MiB item limit. A single measured application
operation may not consume more than 700 RU.

Representative traffic is defined as 60 same-day list renders, one renewal render, one cache-miss
add, one cache-hit add, two removes, ten channel refreshes, and two complete share cycles per
minute. The measured total must
remain at or below 700 RU/s, preserving the documented 30% reserve on the 1,000 RU/s shared
database. These bounds describe the supported single-instance hobby deployment; they are not a
general capacity claim.

Run the emulator evidence after the build and the non-provider and LocalDB suites:

```powershell
$env:YOUTUBED_RUN_COSMOS_TESTS = "true"
dotnet test youtubed.sln --no-build `
    --filter "FullyQualifiedName~CosmosReleaseEnvelopeIntegrationTests" `
    --logger "console;verbosity=detailed"
```

Keep this focused test's detailed output as the emulator evidence. It contains aggregate shape names,
serialized byte counts, request shape, RU, and latency, but no tokens, passwords, connection
strings, document bodies, resource IDs, or raw diagnostics.

## Approved Azure Test Database

Azure validation is manual and must use an explicitly approved, isolated test database. Do not run
it against production data. Before application writes, run the read-only control-plane check:

```powershell
./scripts/test-cosmos-azure-release.ps1 `
    -SubscriptionId "<subscription-id>" `
    -ResourceGroup "<resource-group>" `
    -AccountName "<cosmos-account>" `
    -DatabaseName "<test-database>" `
    -AppServiceName "<test-app>" `
    -EvidencePath ".local/azure/cosmos-release-shape.json"
```

The script verifies free-tier enablement, one region, non-serverless mode, exactly 1,000 RU/s
manual database throughput, exactly the three expected containers, their partition key, TTL and
indexing policies (including the absence of composite, spatial, vector, and full-text indexes or
vector/full-text policies), and a one-instance App Service plan with no enabled autoscale setting.
The sanitized JSON records the TTL and complete indexing summary for each container. It makes
only Azure control-plane reads and writes only the optional local evidence file, creating its
parent directory when needed. The scoped `.local/azure` directory is ignored. The application
startup validator separately proves that every
container inherits database throughput; Azure CLI reports inherited throughput as the absence of
a container offer rather than a stable structured value.

After explicit approval to write test data, configure only the test App Service slot with the
Cosmos provider and its secret configuration, then restart it. Startup must succeed without
creating resources. Exercise these flows with non-sensitive records:

1. create and authenticate a list, render it twice on the same UTC day, then force one daily
   renewal in a controlled test clock or test host;
2. discover and add a channel, render its videos, force refresh, remove it, and add it again;
3. create, list, consume, and delete a share link, confirming a second consume does not disclose
   the list token;
4. delete the list and confirm the anonymous URL no longer resolves.

Keep `Microsoft` logging at `Warning` and enable `Information` only for
`youtubed.Persistence.Cosmos`; capture only structured `CosmosRequest` events. Compare the
operation/container request sequence
with the emulator output and confirm each Azure RU charge stays within the checked-in envelope;
latency and RU need not be equal. Confirm that no request remains unhandled after SDK retry and
that no log contains tokens, share passwords, keys, connection strings, bodies, or raw Cosmos
diagnostics.

Finally, in a second explicitly approved disposable database, create one container with a wrong
partition key, TTL, or indexing policy and start the application in a non-development environment.
Record the safe startup failure, then discard the database. This deliberate Azure mutation is not
performed by the read-only script.

Record the safe evidence JSON, emulator output, Azure smoke-test results, drift-test result, and
the App Service single-instance check in the operator notes. Never record credentials.
