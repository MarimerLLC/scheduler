using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using Scheduler.Core;
using Scheduler.Core.Email;
using Scheduler.Core.Model;
using Scheduler.Core.Security;
using Scheduler.Core.Services;

namespace Scheduler.Data;

/// <summary>EF Core implementation of <see cref="IAccountService"/>. Address changes are audited with no event ID.</summary>
public sealed class AccountService(
    IDbContextFactory<SchedulerDbContext> dbFactory,
    IClock clock,
    IOptions<SchedulingOptions> options,
    IEmailComposer composer) : IAccountService
{
    private const string InvalidLink = "This link has expired or has already been used. Ask for a new one.";
    private const int ReturnUrlMax = 1000;

    private static readonly InstantPattern DisplayPattern = InstantPattern.CreateWithInvariantCulture("uuuu-MM-dd");

    public async Task<IReadOnlyList<AccountAddressView>> ListAddressesAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        if (account is null)
            return caller.VerifiedEmails.Select(e => new AccountAddressView(e, null, SignedInWith: true)).ToList();

        return account.Emails
            .OrderBy(e => e.Email, StringComparer.Ordinal)
            .Select(e => new AccountAddressView(e.Email, DisplayPattern.Format(e.VerifiedAt), caller.VerifiedEmails.Contains(e.Email)))
            .ToList();
    }

    public async Task RequestAddressAsync(CallerIdentity caller, string email, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        var address = EmailAddresses.Normalize(email);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken)
            ?? await SchedulingService.ProvisionAccountAsync(db, caller, now, cancellationToken);

        var own = SchedulingService.OwnEmails(caller, account);
        if (own.Contains(address))
            throw new SchedulingValidationException($"{address} is already on your account.");
        if (own.Count >= VerificationLinks.MaxAddressesPerAccount)
            throw new SchedulingValidationException(
                $"An account can have at most {VerificationLinks.MaxAddressesPerAccount} addresses. Remove one first.");

        var hourAgo = now - Duration.FromHours(1);
        if (await db.EmailVerifications.CountAsync(v => v.UserId == account.Id && v.CreatedAt > hourAgo, cancellationToken)
            >= VerificationLinks.MaxPerAccountPerHour)
            throw new SchedulingValidationException("You've asked for a lot of confirmation links. Try again in an hour.");

        await QueueLinkAsync(db, address, EmailVerificationPurpose.AddAddress, now, cancellationToken,
            link => composer.AddressVerificationAsync(address, link, caller.VerifiedEmails[0]),
            verification => verification.UserId = account.Id);
        Audit(db, caller, "address.requested", new { email = address }, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> ConfirmAddressAsync(CallerIdentity caller, string token, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var verification = await FindUsableAsync(db, token, EmailVerificationPurpose.AddAddress, now, cancellationToken)
            ?? throw new SchedulingValidationException(InvalidLink);

        // Only the account that asked can take the address, so a person tricked into clicking someone else's
        // link doesn't hand their address (and their invitations) to that account.
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        if (account is null || account.Id != verification.UserId)
            throw new SchedulingForbiddenException(
                "This link adds the address to a different account. Sign in as the person who asked for it, then open the link again.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ConsumeAsync(db, verification, now, cancellationToken))
            throw new SchedulingValidationException(InvalidLink);

        var owner = await db.UserEmails.Where(e => e.Email == verification.Email).Select(e => (Guid?)e.UserId).FirstOrDefaultAsync(cancellationToken);
        if (owner is not null && owner != account.Id)
            throw new SchedulingConflictException(
                $"{verification.Email} is already on another account here. Remove it from that account first.");
        if (owner is null)
        {
            if (account.Emails.Count >= VerificationLinks.MaxAddressesPerAccount)
                throw new SchedulingValidationException(
                    $"An account can have at most {VerificationLinks.MaxAddressesPerAccount} addresses. Remove one first.");
            db.UserEmails.Add(new UserEmail { Email = verification.Email, UserId = account.Id, VerifiedAt = now });
            Audit(db, caller, "address.added", new { email = verification.Email }, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another account claimed the address at the same moment.
            throw new SchedulingConflictException(
                $"{verification.Email} is already on another account here. Remove it from that account first.");
        }
        await transaction.CommitAsync(cancellationToken);
        return verification.Email;
    }

    public async Task RemoveAddressAsync(CallerIdentity caller, string email, CancellationToken cancellationToken)
    {
        RequireSignedInOnWeb(caller);
        var address = EmailAddresses.Normalize(email);
        if (caller.VerifiedEmails.Contains(address))
            throw new SchedulingValidationException(
                $"You're signed in with {address}. Sign in with another of your addresses to remove it.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await SchedulingService.FindAccountAsync(db, caller, cancellationToken);
        var owned = account?.Emails.FirstOrDefault(e => e.Email == address)
            ?? throw new SchedulingValidationException($"{address} isn't on your account.");

        db.UserEmails.Remove(owned);
        Audit(db, caller, "address.removed", new { email = address }, clock.GetCurrentInstant());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestSignInLinkAsync(string email, string? displayName, string returnUrl, CancellationToken cancellationToken)
    {
        var address = EmailAddresses.Normalize(email);
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (name is { Length: > FieldLimits.DisplayNameMax })
            name = name[..FieldLimits.DisplayNameMax];
        var local = returnUrl is { Length: > 0 and <= ReturnUrlMax } && returnUrl.StartsWith('/') ? returnUrl : "/";

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        await QueueLinkAsync(db, address, EmailVerificationPurpose.SignIn, now, cancellationToken,
            link => composer.SignInLinkAsync(address, link),
            verification =>
            {
                verification.DisplayName = name;
                verification.ReturnUrl = local;
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<EmailSignIn> ConfirmSignInAsync(string token, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetCurrentInstant();
        var verification = await FindUsableAsync(db, token, EmailVerificationPurpose.SignIn, now, cancellationToken)
            ?? throw new SchedulingValidationException(InvalidLink);

        if (!await ConsumeAsync(db, verification, now, cancellationToken))
            throw new SchedulingValidationException(InvalidLink);
        return new EmailSignIn(verification.Email, verification.DisplayName, verification.ReturnUrl ?? "/");
    }

    public async Task<VerificationView?> FindVerificationAsync(string token, CancellationToken cancellationToken)
    {
        if (!VerificationLinks.IsWellFormed(token))
            return null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hash = VerificationLinks.Hash(token);
        var now = clock.GetCurrentInstant();
        return await db.EmailVerifications.AsNoTracking()
            .Where(v => v.TokenHash == hash && v.ConsumedAt == null && v.ExpiresAt > now)
            .Select(v => new VerificationView(v.Email, v.Purpose))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a verification and its email (the caller saves both). Refuses suppressed addresses, and addresses that
    /// were sent too many links in the last hour, whoever asked.
    /// </summary>
    private async Task QueueLinkAsync(
        SchedulerDbContext db,
        string address,
        EmailVerificationPurpose purpose,
        Instant now,
        CancellationToken cancellationToken,
        Func<string, Task<ComposedEmail>> compose,
        Action<EmailVerification> describe)
    {
        if (await db.Suppressions.AnyAsync(s => s.Email == address, cancellationToken))
            throw new SchedulingValidationException($"This server has been asked not to email {address}.");

        var hourAgo = now - Duration.FromHours(1);
        if (await db.EmailVerifications.CountAsync(v => v.Email == address && v.CreatedAt > hourAgo, cancellationToken)
            >= VerificationLinks.MaxPerAddressPerHour)
            throw new SchedulingValidationException(
                $"Several links were sent to {address} in the last hour. Use the newest one, or try again later.");

        var token = VerificationLinks.New();
        var verification = new EmailVerification
        {
            Email = address,
            Purpose = purpose,
            TokenHash = VerificationLinks.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + VerificationLinks.Lifetime,
        };
        describe(verification);
        db.EmailVerifications.Add(verification);

        var email = await compose(VerificationLinks.Url(options.Value.PublicBaseUrl, token));
        db.Outbox.Add(new OutboxEmail
        {
            To = address,
            Subject = email.Subject,
            TextBody = email.TextBody,
            HtmlBody = email.HtmlBody,
            CreatedAt = now,
            NextAttemptAt = now,
        });
    }

    private static async Task<EmailVerification?> FindUsableAsync(
        SchedulerDbContext db, string token, EmailVerificationPurpose purpose, Instant now, CancellationToken cancellationToken)
    {
        if (!VerificationLinks.IsWellFormed(token))
            return null;
        var hash = VerificationLinks.Hash(token);
        var verification = await db.EmailVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.TokenHash == hash, cancellationToken);
        return verification is not null && verification.Purpose == purpose && verification.IsUsable(now) ? verification : null;
    }

    /// <summary>Marks a link used, unless a concurrent request got there first.</summary>
    private static async Task<bool> ConsumeAsync(SchedulerDbContext db, EmailVerification verification, Instant now, CancellationToken cancellationToken) =>
        await db.EmailVerifications
            .Where(v => v.Id == verification.Id && v.ConsumedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.ConsumedAt, now), cancellationToken) == 1;

    /// <summary>Addresses are managed by a person in the browser, never by an agent or a voting link.</summary>
    private static void RequireSignedInOnWeb(CallerIdentity caller)
    {
        if (caller.Channel != Channel.Web || caller.MagicLinkEventId is not null || caller.VerifiedEmails.Count == 0)
            throw new SchedulingForbiddenException("Your addresses can only be managed on the web, signed in.");
    }

    private static void Audit(SchedulerDbContext db, CallerIdentity caller, string action, object detail, Instant now) =>
        db.Audit.Add(new AuditEntry
        {
            At = now,
            Actor = caller.VerifiedEmails.FirstOrDefault(),
            Channel = caller.Channel,
            Action = action,
            Detail = JsonSerializer.Serialize(detail),
        });
}
