using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Data.Tests;

/// <summary>
/// A <see cref="SchedulingService"/> over a fresh migrated database, with a fake clock fixed at
/// Fri Oct 16, 2026 10:00 CDT, and the cast of callers the tests use.
/// </summary>
internal sealed class Harness
{
    public const string BaseUrl = "https://sched.test";

    public static readonly CallerIdentity Rocky = Caller("rocky@example.com");
    public static readonly CallerIdentity Bob = Caller("bob@example.com");
    public static readonly CallerIdentity Carol = Caller("carol@example.com");
    public static readonly CallerIdentity Dave = Caller("dave@example.com");
    public static readonly CallerIdentity Mallory = Caller("mallory@example.com");
    public static readonly CallerIdentity Admin = Caller("admin@example.com");

    private Harness(IDbContextFactory<SchedulerDbContext> db, FakeClock clock, MagicLinks links, Action<SchedulingOptions>? configure)
    {
        Db = db;
        Clock = clock;
        Links = links;
        var settings = new SchedulingOptions
        {
            Organizers = ["rocky@example.com", "*@marimer.llc"],
            Admins = ["admin@example.com"],
            PublicBaseUrl = BaseUrl,
        };
        configure?.Invoke(settings);
        var options = Options.Create(settings);
        var composer = new PlainTextEmailComposer(BaseUrl + "/mcp");
        Service = new SchedulingService(db, clock, options, links, composer, Changes);
        Jobs = new SchedulingJobs(db, clock, options, links, composer, Changes);
        Subscriptions = new SubscriptionService(db, clock, options, composer);
        AdminService = new AdminService(db, clock, options);
        AccessTokens = new AccessTokenService(db, clock);
        Accounts = new AccountService(db, clock, options, new PlainTextEmailComposer(BaseUrl + "/mcp"));
    }

    public IDbContextFactory<SchedulerDbContext> Db { get; }
    public FakeClock Clock { get; }
    public MagicLinks Links { get; }
    public SchedulingService Service { get; }
    public AdminService AdminService { get; }
    public AccessTokenService AccessTokens { get; }
    public AccountService Accounts { get; }
    public SchedulingJobs Jobs { get; }
    public SubscriptionService Subscriptions { get; }

    /// <summary>Records every change the services publish.</summary>
    public RecordingEventChanges Changes { get; } = new();

    public static CallerIdentity Caller(string email) => new(null, [email], Channel.Mcp);

    /// <param name="configure">Changes to the options, e.g. lower limits.</param>
    public static async Task<Harness> CreateAsync(Action<SchedulingOptions>? configure = null)
    {
        var options = new DbContextOptionsBuilder<SchedulerDbContext>()
            .UseNpgsql(PostgresContainer.NewDatabase(), npgsql => npgsql.UseNodaTime())
            .Options;
        var factory = new Factory(options);

        await using (var db = factory.CreateDbContext())
            await db.Database.MigrateAsync();

        return new Harness(factory, new FakeClock(Instant.FromUtc(2026, 10, 16, 15, 0)), new MagicLinks(MagicLinks.NewKey()), configure);
    }

    /// <summary>
    /// Planning, 30 minutes, organized by Rocky in Chicago: Bob and Carol required, Dave optional;
    /// Tue Nov 3, Wed Nov 4 and Thu Nov 5 at 10:00; vote by Fri Oct 30 17:00.
    /// </summary>
    public static CreateEventRequest Request(
        IReadOnlyList<ParticipantInput>? participants = null,
        IReadOnlyList<SlotInput>? slots = null,
        string? respondBy = "2026-10-30T17:00",
        string title = "Planning") =>
        new(
            title,
            "Q4 roadmap",
            30,
            "America/Chicago",
            participants ??
            [
                new("bob@example.com", "Bob"),
                new("carol@example.com", "Carol"),
                new("dave@example.com", "Dave", AttendeeRole.Optional),
            ],
            slots ??
            [
                new("2026-11-03T10:00", "America/Chicago"),
                new("2026-11-04T10:00", "America/Chicago"),
                new("2026-11-05T10:00", "America/Chicago"),
            ],
            respondBy,
            "Rocky Lhotka");

    public Task<EventView> CreateAsync(CreateEventRequest? request = null) =>
        Service.CreateEventAsync(Rocky, request ?? Request(), CancellationToken.None);

    public Task<EventView> VoteAsync(CallerIdentity caller, EventView ev, params (int Slot, VoteValue Value)[] votes) =>
        Service.VoteAsync(caller, ev.ShortCode, votes.Select(v => new VoteInput(ev.Slots[v.Slot].SlotId, v.Value)).ToList(), null, CancellationToken.None);

    public Task<EventView> GetAsync(CallerIdentity caller, EventView ev) =>
        Service.GetEventAsync(caller, ev.Id.ToString(), null, CancellationToken.None);

    public async Task<List<OutboxEmail>> OutboxAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.Outbox.OrderBy(o => o.Id).ToListAsync();
    }

    public async Task<List<AuditEntry>> AuditAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.Audit.OrderBy(a => a.Id).ToListAsync();
    }

    public async Task WithDbAsync(Func<SchedulerDbContext, Task> action)
    {
        await using var db = await Db.CreateDbContextAsync();
        await action(db);
        await db.SaveChangesAsync();
    }

    internal sealed class RecordingEventChanges : IEventChanges
    {
        private readonly EventChanges _inner = new();

        public List<Guid> Published { get; } = [];

        public void Publish(Guid eventId)
        {
            Published.Add(eventId);
            _inner.Publish(eventId);
        }

        public IDisposable Subscribe(Action<Guid> handler) => _inner.Subscribe(handler);
    }

    private sealed class Factory(DbContextOptions<SchedulerDbContext> options) : IDbContextFactory<SchedulerDbContext>
    {
        public SchedulerDbContext CreateDbContext() => new(options);
    }
}
