# Scheduler

Find a meeting time across people and organizations without anyone sharing their calendar.

The organizer (or their AI agent) proposes candidate times that already work for them. Everyone invited
votes **preferred**, **if necessary** or **no** on each time, from whichever MCP-capable agent they use
(Claude Desktop, Claude Code, GitHub Copilot, RockBot, ...) or from a web page linked in the invitation
email. The server never reads a calendar: each person's own agent checks their own calendar, and the
server only ever learns the votes.

See [docs/design.md](docs/design.md) for the full design.

## Status

In place and tested:

- Domain model, state machine, slot ranking, time-zone handling (NodaTime), voting-link tokens
- `SchedulingService`: every operation, with audit entries and queued notification emails
- EF Core model and the initial migration for PostgreSQL
- MCP endpoint (`/mcp`) with every tool, both prompts and the server instructions
- Email outbox dispatcher and SMTP sender (Mailpit in development), plain-text notifications
- Aspire AppHost for local development

Not yet: sign-in (only the Development `X-Dev-Email` header works), the voting and organizer pages, HTML
email templates and unsubscribe links, and the reminder, deadline and purge jobs. The roadmap is in the
design doc.

## Layout

| Path | What |
|---|---|
| `src/Scheduler.Core` | Domain model and rules. No infrastructure dependencies. |
| `src/Scheduler.Data` | EF Core `SchedulerDbContext` and the `ISchedulingService` implementation |
| `src/Scheduler.Web` | ASP.NET Core host: Blazor UI, MCP endpoint, email, background workers |
| `src/Scheduler.ServiceDefaults` | Aspire defaults: OpenTelemetry, health checks, resilience |
| `src/Scheduler.AppHost` | Aspire AppHost for local development (PostgreSQL + Mailpit) |
| `tests/Scheduler.Core.Tests` | MSTest unit tests for the domain rules |
| `tests/Scheduler.Data.Tests` | Integration tests for `SchedulingService` against PostgreSQL (Testcontainers) |
| `docs/design.md` | Design document |

## Build and test

Requires the .NET 10 SDK (pinned by `global.json`).

```sh
dotnet build Scheduler.slnx
dotnet test Scheduler.slnx
```

`Scheduler.Data.Tests` needs Docker: it starts a PostgreSQL container and gives each test its own freshly
migrated database.

## Migrations

`dotnet-ef` is pinned in the repo's tool manifest.

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Scheduler.Data --startup-project src/Scheduler.Data --output-dir Migrations
```

In Development the web app applies migrations at startup.

## Run locally

Requires Docker (Aspire starts PostgreSQL and Mailpit containers).

```sh
dotnet run --project src/Scheduler.AppHost
```

The Aspire dashboard links to the web app, pgAdmin and the Mailpit inbox. In Development:

- an MCP client can act as any address without signing in by sending an `X-Dev-Email` header (ignored in
  every other environment);
- anyone may organize (`Scheduler:Organizers` is `["*"]` in `appsettings.Development.json`);
- voting links use a random key that changes on every restart unless `Scheduler:MagicLinkKey` is set.

## Line endings

All text files are LF in the repo and in the working tree, on every platform (`.gitattributes` sets
`eol=lf`, overriding `core.autocrlf`; `.editorconfig` makes editors create LF files). Only `.bat` and
`.cmd` files are CRLF.

## License

[MIT](LICENSE) © Marimer LLC
