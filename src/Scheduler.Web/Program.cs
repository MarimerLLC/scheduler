using Microsoft.Extensions.Options;
using Scheduler.Core.Email;
using Scheduler.Data;
using Scheduler.Web.Components;
using Scheduler.Web.Email;
using Scheduler.Web.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString("schedulerdb")
    ?? throw new InvalidOperationException("Connection string 'schedulerdb' is not configured.");
builder.Services.AddSchedulerData(connectionString);

// Email: SMTP when an endpoint is configured (Mailpit under Aspire), otherwise log-only.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var smtpEndpoint = SmtpEmailSender.EndpointFromConnectionString(builder.Configuration.GetConnectionString("mailpit"));
if (smtpEndpoint is not null)
    builder.Services.AddSingleton<IEmailSender>(sp => new SmtpEmailSender(smtpEndpoint, sp.GetRequiredService<IOptions<EmailOptions>>()));
else
    builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
builder.Services.AddHostedService<OutboxDispatcher>();

// Identity is not wired up yet (docs/design.md, "Identity"). With no scheme registered, any endpoint that
// requires authorization fails closed.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services
    .AddMcpServer(SchedulerMcpOptions.Configure)
    // Stateless: no tool needs a server-to-client request or session state, and any replica can serve
    // any call. Revisit when adding the optional wait_for_update task (docs/design.md, "MCP Tasks").
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SchedulingTools>()
    .WithPrompts<SchedulingPrompts>();

var app = builder.Build();

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

app.MapDefaultEndpoints();

app.Run();
