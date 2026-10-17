using Scheduler.Core;
using Scheduler.Core.Services;

namespace Scheduler.Web.Email;

/// <summary>
/// The List-Unsubscribe URL. Mail clients that support RFC 8058 POST to it (body
/// <c>List-Unsubscribe=One-Click</c>, no cookies, no antiforgery token); older ones open it in a browser, which
/// gets the confirmation page instead. The page for people is <c>/unsubscribe</c> (Unsubscribe.razor).
/// </summary>
internal static class UnsubscribeEndpoints
{
    public static void MapUnsubscribe(this WebApplication app)
    {
        app.MapPost(UnsubscribeLinks.OneClickPath, async (string? t, UnsubscribeLinks links, ISubscriptionService subscriptions, CancellationToken cancellationToken) =>
        {
            if (links.Read(t) is not { } email)
                return Results.BadRequest("This unsubscribe link isn't valid.");
            try
            {
                await subscriptions.UnsubscribeAsync(email, cancellationToken);
            }
            catch (SchedulingValidationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.Text($"{email} won't get any more email from this server.");
        }).DisableAntiforgery();

        app.MapGet(UnsubscribeLinks.OneClickPath, (string? t) =>
            Results.Redirect(UnsubscribeLinks.PagePath + QueryString.Create("t", t ?? "")));
    }
}
