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

// Browser sign-in (Microsoft, Google), the OAuth authorization server for MCP clients, and bearer tokens on
// /mcp. See docs/design.md, "Identity and authentication".
builder.AddSchedulerIdentity();
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

app.MapMcp(McpResource.Path).RequireAuthorization(IdentitySetup.McpPolicy);

app.MapSchedulerIdentity();
app.MapDefaultEndpoints();

await app.RunAsync();
