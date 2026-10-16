# Project: Scheduler

Meeting-time coordination across people and organizations: an MCP server + Blazor web UI + outbound
email. The design is in `docs/design.md`; read the relevant section before changing behavior, and update
it when a decision changes.

## Build and test

- `dotnet build Scheduler.slnx`
- `dotnet test Scheduler.slnx` (`Scheduler.Data.Tests` needs Docker for its PostgreSQL container)
- Local run (needs Docker): `dotnet run --project src/Scheduler.AppHost`
- New migration: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/Scheduler.Data --startup-project src/Scheduler.Data --output-dir Migrations`

## Conventions

- Every scheduling rule lives in `ISchedulingService` (`Scheduler.Core`, implemented in `Scheduler.Data`).
  MCP tools and Blazor pages stay thin wrappers so agents and people get the same behavior.
- Times are NodaTime. A slot's source of truth is local wall time + IANA zone; the `Instant` is derived.
  All local-to-UTC conversion goes through `SlotTimeResolver`. Never accept zone abbreviations or fixed offsets.
- User-supplied text has hard limits in `FieldLimits`; it is read by other people's agents (prompt injection).
- Errors meant for the caller are `SchedulingException` subclasses; tools surface their messages verbatim.
- Every state change writes an `AuditEntry` and its `OutboxEmail` rows in the same `SaveChanges`.
- Organizer edits take the event `Version` and call `Touch` so the event row is always rewritten (that is
  what moves `xmin`). Votes never touch the event row.
- GUID keys are assigned in code; keep `ValueGeneratedNever()` on them or new child rows save as updates.
- Use `IDbContextFactory<SchedulerDbContext>`, never an injected `DbContext` (Blazor Server circuits are long-lived).
- Tests: MSTest. Domain rules get unit tests in `tests/Scheduler.Core.Tests`; service behavior gets
  integration tests in `tests/Scheduler.Data.Tests` (real PostgreSQL, `Harness` sets up the cast and clock).

## Environment notes

- Developed on Windows with Git Bash. Files are LF everywhere (`.gitattributes` `eol=lf`); write LF.
- When running kubectl or docker with Unix-style paths from Git Bash, prefix `MSYS_NO_PATHCONV=1`.
