# Task 001b: Create Domain Models

Status: Not Started

Depends On: 0100_document_target_architecture

## Goal

Introduce storage-agnostic domain objects so services and repository interfaces can move away from Dapper-shaped models before provider-specific SQL and Cosmos implementations diverge.

## Scope

- Add `Domain` types for list, list channel projection, list video projection, channel, channel video, share link, worker state, channel status, and status reason.
- Keep domain read models storage-agnostic and use-case shaped.
- Model `ListVideoProjection` as a hierarchy of channels with nested videos.
- Leave SQL rows and future Cosmos documents in provider-specific persistence layers.
- Add focused unit tests for simple domain behavior if useful.

## Out Of Scope

- Rewriting repositories to return the new domain types.
- SQL schema changes.
- Cosmos implementation.
- Worker rewrite.
- App-wide clock replacement.

## Validation

- Run unit tests.

## Implementation Summary

Not completed.
