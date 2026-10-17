# Scheduler

Find a meeting time across people and organizations without anyone sharing their calendar.

The organizer (or their AI agent) proposes candidate times that already work for them. Everyone invited
votes **preferred**, **if necessary** or **no** on each time, from whichever MCP-capable agent they use
(Claude Desktop, Claude Code, GitHub Copilot, RockBot, ...) or from a web page linked in the invitation
email. The server never reads a calendar: each person's own agent checks their own calendar, and the
server only ever learns the votes.

See [docs/design.md](docs/design.md) for the full design, and
[docs/identity-setup.md](docs/identity-setup.md) for setting up sign-in on a deployed server.

## Status

In place and tested:

- Domain model, state machine, slot ranking, time-zone handling (NodaTime), voting-link tokens
- `SchedulingService`: every operation, with audit entries and queued notification emails
- EF Core model and the initial migration for PostgreSQL
- MCP endpoint (`/mcp`) with every tool, both prompts and the server instructions
- Administration: admins (from configuration) manage who may organize, through admin-only MCP tools or
  the `/admin/organizers` page
- Email outbox dispatcher and SMTP sender (Mailpit in development), plain-text notifications
- Sign-in: Microsoft and Google for the browser, and OAuth for MCP clients (this app is its own
  authorization server, federating to the same providers; clients register with a client ID metadata
  document or dynamic client registration), plus personal access tokens for headless agents such as
  RockBot, managed on `/account`
- Aspire AppHost for local development

Not yet: the voting and organizer pages, HTML email templates
and unsubscribe links, and the reminder, deadline and purge jobs. The roadmap is in the design doc.

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
| `tests/Scheduler.Web.Tests` | The MCP OAuth flow end to end with the MCP SDK client, plus the identity rules |
| `docs/design.md` | Design document |
| `docs/identity-setup.md` | Operations: Microsoft and Google app registrations, token certificates, Kubernetes settings |

## Build and test

Requires the .NET 10 SDK (pinned by `global.json`).

```sh
dotnet build Scheduler.slnx
dotnet test Scheduler.slnx
```

`Scheduler.Data.Tests` and `Scheduler.Web.Tests` need Docker: each starts a PostgreSQL container and gives
each test its own freshly migrated database.

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

- an MCP client can act as any address without signing in by sending an `X-Dev-Email` header, and the
  browser can sign in as any address at `/dev/signin` (both ignored in every other environment). An MCP
  client without the header goes through the real OAuth flow, with the development sign-in offered on
  `/signin`, so connecting Claude Code or VS Code to `http://localhost:<port>/mcp` works locally;
- Microsoft and Google sign-in appear once `Identity:Microsoft` or `Identity:Google` has a `ClientId` and
  `ClientSecret` (user secrets), with `/signin/callback/microsoft` or `/signin/callback/google` registered as
  the redirect URI;
- anyone may organize (`Scheduler:Organizers` is `["*"]` in `appsettings.Development.json`), and
  `admin@localhost.test` is an admin, so `/admin/organizers` and the admin MCP tools can be tried;
- voting links use a random key that changes on every restart unless `Scheduler:MagicLinkKey` is set.

## Line endings

All text files are LF in the repo and in the working tree, on every platform (`.gitattributes` sets
`eol=lf`, overriding `core.autocrlf`; `.editorconfig` makes editors create LF files). Only `.bat` and
`.cmd` files are CRLF.

## License

[MIT](LICENSE) © Marimer LLC
