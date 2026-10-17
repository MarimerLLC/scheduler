# Project: Scheduler

Meeting-time coordination across people and organizations: an MCP server + Blazor web UI + outbound
email. The design is in `docs/design.md`; read the relevant section before changing behavior, and update
it when a decision changes.

## Build and test

- `dotnet build Scheduler.slnx`
- `dotnet test Scheduler.slnx` (`Scheduler.Data.Tests` and `Scheduler.Web.Tests` need Docker for PostgreSQL)
- Local run (needs Docker): `dotnet run --project src/Scheduler.AppHost`
- Deploy: `k8s/build.sh <version>` then `k8s/deploy.sh <version>`, per `docs/deployment.md`. Never apply
  `k8s/secret.example.yaml`; secrets are made by hand.
- New migration: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/Scheduler.Data --startup-project src/Scheduler.Data --output-dir Migrations`

## Conventions

- Every scheduling rule lives in `ISchedulingService`, every administration rule in `IAdminService`, every
  personal access token rule in `IAccessTokenService`, every address and emailed-link rule in
  `IAccountService`, every unsubscribe rule in `ISubscriptionService`, and every background job rule in
  `ISchedulingJobs` (all in `Scheduler.Core`, implemented in `Scheduler.Data`). MCP tools and Blazor pages stay thin wrappers
  so agents and people get the same behavior; hiding a tool or a button is never the access check.
- Times are NodaTime. A slot's source of truth is local wall time + IANA zone; the `Instant` is derived.
  All local-to-UTC conversion goes through `SlotTimeResolver`. Never accept zone abbreviations or fixed offsets.
- User-supplied text has hard limits in `FieldLimits`; it is read by other people's agents (prompt injection).
- Errors meant for the caller are `SchedulingException` subclasses; tools surface their messages verbatim.
- Every state change writes an `AuditEntry` and its `OutboxEmail` rows in the same `SaveChanges`, then
  calls `IEventChanges.Publish` so open pages update.
- Email wording: subjects and plain text in `PlainTextEmailComposer` (Core), the HTML alternative in
  `HtmlEmailComposer` and the Razor templates in `src/Scheduler.Web/Email/Templates`. Change both together.
- Organizer edits take the event `Version` and call `Touch` so the event row is always rewritten (that is
  what moves `xmin`). Votes never touch the event row.
- GUID keys are assigned in code; keep `ValueGeneratedNever()` on them or new child rows save as updates.
- Use `IDbContextFactory<SchedulerDbContext>`, never an injected `DbContext` (Blazor Server circuits are long-lived).
- Tests: MSTest. Domain rules get unit tests in `tests/Scheduler.Core.Tests`; service behavior gets
  integration tests in `tests/Scheduler.Data.Tests` (real PostgreSQL, `Harness` sets up the cast and clock);
  sign-in and the MCP OAuth flow are tested through the real app in `tests/Scheduler.Web.Tests` (`SchedulerApp`).
- Identity: every caller is a verified email address in a claim (`CallerResolver`). Whatever signs people
  in must only issue verified addresses; an address a provider didn't verify goes through an emailed link
  (`EmailLinkSignIn`, `/verify`). The OAuth issuer and MCP resource URI come from the request
  (`McpResource`), never from configuration.

## Environment notes

- Developed on Windows with Git Bash. Files are LF everywhere (`.gitattributes` `eol=lf`); write LF.
- When running kubectl or docker with Unix-style paths from Git Bash, prefix `MSYS_NO_PATHCONV=1`.
