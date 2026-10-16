using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Scheduler.Core.Email;
using Scheduler.Web;
using Scheduler.Web.Components;
using Scheduler.Web.Email;
using Scheduler.Web.Identity;
using Scheduler.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddScheduler();

// Email: SMTP when an endpoint is configured (Mailpit under Aspire), otherwise log-only.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var smtpEndpoint = SmtpEmailSender.EndpointFromConnectionString(builder.Configuration.GetConnectionString("mailpit"));
if (smtpEndpoint is not null)
    builder.Services.AddSingleton<IEmailSender>(sp => new SmtpEmailSender(smtpEndpoint, sp.GetRequiredService<IOptions<EmailOptions>>()));
else
    builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
builder.Services.AddHostedService<OutboxDispatcher>();

// Browser sign-in is a cookie. Until OIDC (roadmap item 3, docs/design.md "Identity") only Development can
// issue one, through DevSignIn. /mcp answers 401 instead of redirecting a client to a sign-in page.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = builder.Environment.IsDevelopment() ? DevSignIn.SignInPath : "/signin";
        options.Cookie.Name = "scheduler.auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services
    .AddMcpServer(SchedulerMcpOptions.Configure)
    // Stateless: no tool needs a server-to-client request or session state, and any replica can serve
    // any call. Revisit when adding the optional wait_for_update task (docs/design.md, "MCP Tasks").
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SchedulingTools>()
    .WithTools<AdminTools>()
    .WithPrompts<SchedulingPrompts>()
    .WithRequestFilters(filters => filters.AddListToolsFilter(AdminTools.HideFromNonAdmins));

var app = builder.Build();

await app.MigrateDevelopmentDatabaseAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

var mcp = app.MapMcp("/mcp");
if (!app.Environment.IsDevelopment())
    mcp.RequireAuthorization();

app.MapDevSignIn();
app.MapDefaultEndpoints();

await app.RunAsync();
