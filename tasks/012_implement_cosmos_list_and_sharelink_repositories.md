# Task 012: Implement Cosmos List And ShareLink Repositories

Status: Not Started

Depends On: 011_implement_cosmos_documents_and_indexes

## Goal

Implement Cosmos list and share-link behavior behind the provider-neutral ports.

## Scope

- Implement list point reads.
- Implement authenticated access with once-per-day renewal and TTL updates.
- Implement list membership add/remove with ETag retry.
- Implement list channel and list video read models from embedded channels.
- Implement share link create/list/consume/delete.
- Use ETag on share-link consume.

## Out Of Scope

- Channel repository.
- Worker state.
- Projection update repository.

## Validation

- Cosmos contract tests for list and share-link behavior pass.

## Implementation Summary

Not completed.
