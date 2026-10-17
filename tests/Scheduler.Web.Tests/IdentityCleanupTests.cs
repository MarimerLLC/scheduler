using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;
using OpenIddict.Abstractions;
using Scheduler.Web.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Scheduler.Web.Tests;

/// <summary>Deleting dynamically registered clients that never signed anyone in.</summary>
[TestClass]
public sealed class IdentityCleanupTests
{
    private SchedulerApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SchedulerApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task A_client_registered_over_a_day_ago_that_was_never_used_is_deleted()
    {
        var fresh = await RegisterAsync();
        var unused = await RegisterAsync();
        var used = await RegisterAsync();
        await RegisteredDaysAgoAsync(unused, 2);
        await RegisteredDaysAgoAsync(used, 2);
        await AuthorizeAsync(used);

        var deleted = await _app.Services.GetRequiredService<IdentityCleanup>().RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(1, deleted);
        Assert.IsNull(await FindAsync(unused));
        Assert.IsNotNull(await FindAsync(fresh), "a client gets a day to sign someone in");
        Assert.IsNotNull(await FindAsync(used), "a client with an authorization is in use");
    }

    private async Task<string> RegisterAsync()
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/connect/register", new
        {
            client_name = "Legacy Agent",
            redirect_uris = new[] { "http://localhost/callback" },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private async Task RegisteredDaysAgoAsync(string clientId, int days)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(clientId) ?? throw new AssertFailedException(clientId);
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        var at = SystemClock.Instance.GetCurrentInstant() - Duration.FromDays(days);
        descriptor.Properties[DynamicClientRegistration.RegisteredAtProperty] = JsonSerializer.SerializeToElement(InstantPattern.ExtendedIso.Format(at));
        await applications.UpdateAsync(application, descriptor);
    }

    private async Task AuthorizeAsync(string clientId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var application = await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync(clientId);
        var applicationId = await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().GetIdAsync(application!);
        await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = applicationId,
            Subject = "pat@example.com",
            Status = Statuses.Valid,
            Type = AuthorizationTypes.Permanent,
            CreationDate = DateTimeOffset.UtcNow,
        });
    }

    private async Task<object?> FindAsync(string clientId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync(clientId);
    }
}
