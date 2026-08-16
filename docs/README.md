# Documentation

This directory describes the simplified SQL Server and Azure Cosmos DB persistence design.
It replaces the earlier recovery-ledger and denormalized-projection design.

Read the documents in this order:

1. [`architecture.md`](architecture.md) defines application behavior, persistence boundaries,
   consistency choices, and the request-driven refresh model.
2. [`youtube-sync-design.md`](youtube-sync-design.md) defines priority, batching, incremental
   playlist reconciliation, YouTube request pacing, and per-channel refresh failure behavior.
3. [`cosmos-data-model.md`](cosmos-data-model.md) defines the three Cosmos containers,
   document shapes, indexing, TTL, concurrency, and capacity limits.
4. [`migration-and-operations.md`](migration-and-operations.md) defines free-tier provisioning,
   the offline SQL-to-Cosmos migration, cutover, rollback, and operating constraints.
5. [`cosmos-release-validation.md`](cosmos-release-validation.md) defines the measured emulator
   release envelope and the manual, secret-safe Azure evidence procedure.

The main design rule is simple: every durable fact has one authoritative owner. The design
does not maintain distributed copies merely to save a few reads.
