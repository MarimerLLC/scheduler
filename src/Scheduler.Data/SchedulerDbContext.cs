using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Scheduler.Core;
using Scheduler.Core.Model;
using Scheduler.Core.Security;

namespace Scheduler.Data;

/// <summary>
/// The scheduling database. Times are NodaTime types mapped natively by the Npgsql NodaTime plugin:
/// <c>Instant</c> to <c>timestamptz</c>, <c>LocalDateTime</c> to <c>timestamp</c>, <c>Duration</c> to <c>interval</c>.
/// Enums are stored as text so the tables read sensibly in psql.
/// </summary>
public sealed class SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<SchedulingEvent> Events => Set<SchedulingEvent>();
    public DbSet<ProposedSlot> Slots => Set<ProposedSlot>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<UserEmail> UserEmails => Set<UserEmail>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<OutboxEmail> Outbox => Set<OutboxEmail>();
    public DbSet<EmailSuppression> Suppressions => Set<EmailSuppression>();
    public DbSet<OrganizerGrant> OrganizerGrants => Set<OrganizerGrant>();
    public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();
    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();
    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();

    /// <summary>ASP.NET Core Data Protection keys (cookies, unsubscribe links), shared by every replica and restart.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Every GUID key is assigned in code (version 7). Telling EF so makes a new child added to a loaded
        // event (a slot, a participant) an insert; otherwise a key that is already set reads as an existing row.
        model.Entity<SchedulingEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Ignore(x => x.ActiveSlots);
            e.HasIndex(x => x.ShortCode).IsUnique();
            e.Property(x => x.ShortCode).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(FieldLimits.TitleMax);
            e.Property(x => x.Description).HasMaxLength(FieldLimits.DescriptionMax);
            e.Property(x => x.OrganizerEmail).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.OrganizerName).HasMaxLength(FieldLimits.DisplayNameMax);
            e.Property(x => x.OrganizerTimeZone).HasMaxLength(FieldLimits.TimeZoneIdMax);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CalendarEventUid).HasMaxLength(512);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.OrganizerId);
            e.HasIndex(x => new { x.State, x.RespondBy });
            e.HasIndex(x => x.ClosedAt);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.OrganizerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Slots).WithOne().HasForeignKey(s => s.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ProposedSlot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TimeZoneId).HasMaxLength(FieldLimits.TimeZoneIdMax);
            e.HasIndex(x => new { x.EventId, x.Start });
        });

        model.Entity<Participant>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Email).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.DisplayName).HasMaxLength(FieldLimits.DisplayNameMax);
            e.Property(x => x.Note).HasMaxLength(FieldLimits.NoteMax);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TokenNonce).HasMaxLength(MagicLinks.NonceBytes);
            e.HasIndex(x => new { x.EventId, x.Email }).IsUnique();
            e.HasIndex(x => x.Email);
            e.HasMany(x => x.Votes).WithOne().HasForeignKey(v => v.ParticipantId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Vote>(e =>
        {
            e.HasKey(x => new { x.ParticipantId, x.SlotId });
            e.Property(x => x.Value).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.HasOne<ProposedSlot>().WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(FieldLimits.DisplayNameMax);
            e.HasMany(x => x.Emails).WithOne().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserEmail>(e =>
        {
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasMaxLength(FieldLimits.EmailMax);
        });

        model.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Detail).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EventId, x.At });
        });

        model.Entity<OutboxEmail>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.To).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.ReplyTo).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.ListUnsubscribeUrl).HasMaxLength(1024);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.ProviderMessageId).HasMaxLength(256);
            e.HasIndex(x => x.NextAttemptAt).HasFilter("\"Status\" = 'Pending'");
            e.HasIndex(x => x.EventId);
        });

        model.Entity<OrganizerGrant>(e =>
        {
            e.HasKey(x => x.Pattern);
            e.Property(x => x.Pattern).HasMaxLength(OrganizerPatterns.MaxLength);
            e.Property(x => x.Note).HasMaxLength(FieldLimits.NoteMax);
            e.Property(x => x.GrantedBy).HasMaxLength(FieldLimits.EmailMax);
        });

        model.Entity<EmailSuppression>(e =>
        {
            e.HasKey(x => x.Email);
            e.Property(x => x.Email).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
        });

        model.Entity<PersonalAccessToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(AccessTokens.NameMax);
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<EmailVerification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Email).HasMaxLength(FieldLimits.EmailMax);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DisplayName).HasMaxLength(FieldLimits.DisplayNameMax);
            e.Property(x => x.ReturnUrl).HasMaxLength(1000);
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.Email, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ServerSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(256);
        });

        // The MCP authorization server's tables: OAuth clients (registered by metadata document or dynamically),
        // authorizations and tokens. See docs/design.md, "MCP".
        model.UseOpenIddict();
    }
}
