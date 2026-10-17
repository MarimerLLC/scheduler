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

// Email: the SMTP relay in Email:Smtp (required outside Development), else Mailpit under Aspire, else (Development
// only) nothing, which leaves emails in the outbox. See docs/deployment.md.
var emailSection = builder.Configuration.GetSection(EmailOptions.SectionName);
builder.Services.Configure<EmailOptions>(emailSection);
var relay = emailSection.Get<EmailOptions>()?.Smtp is { IsConfigured: true } configured
    ? configured
    : SmtpEmailSender.FromConnectionString(builder.Configuration.GetConnectionString("mailpit"));
if (relay is not null)
    builder.Services.AddSingleton<IEmailSender>(sp => new SmtpEmailSender(relay, sp.GetRequiredService<IOptions<EmailOptions>>()));
else if (builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
else
    throw new InvalidOperationException("Email:Smtp:Host is not configured. Set Email:Smtp (host, port, username, password) to the relay that sends this server's email.");
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<SchedulingJobRunner>();

// Browser sign-in (Microsoft, Google), the OAuth authorization server for MCP clients, and bearer tokens on
// /mcp. See docs/design.md, "Identity and authentication".
builder.AddSchedulerIdentity();
builder.AddSchedulerRateLimits();
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
// Friendly error pages for people, not for the protocol endpoints: an MCP client needs the bare 401 and its
// WWW-Authenticate header, and OAuth errors have their own bodies.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/mcp") && !context.Request.Path.StartsWithSegments("/connect"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// The rate limiter runs after authorization, so /mcp is limited per caller rather than per IP.
app.MapMcp(McpResource.Path).RequireAuthorization(IdentitySetup.McpPolicy).RequireRateLimiting(RateLimits.Mcp);

app.MapSchedulerIdentity();
app.MapUnsubscribe();
app.MapDefaultEndpoints();

await app.RunAsync();
