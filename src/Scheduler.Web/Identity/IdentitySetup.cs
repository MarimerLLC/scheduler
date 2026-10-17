using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using OpenIddict.Client;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using Scheduler.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Scheduler.Web.Identity;

/// <summary>
/// Who can sign in, and how: a cookie for the browser (Microsoft or Google sign-in), an OAuth authorization
/// server for MCP clients (OpenIddict, federating the actual login to the same providers), and bearer
/// tokens on <c>/mcp</c>. See docs/design.md, "Identity and authentication".
/// </summary>
internal static class IdentitySetup
{
    /// <summary>The authorization policy on <c>/mcp</c>: a bearer token for this server (or, in Development, the dev header).</summary>
    public const string McpPolicy = "mcp";

    /// <summary>
    /// Authenticates a personal access token or, for any other bearer value, with OpenIddict's token
    /// validation; challenges with the MCP 401 (resource metadata).
    /// </summary>
    private const string McpBearerScheme = "McpBearer";

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

    public static void AddSchedulerIdentity(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(SchedulerIdentityOptions.SectionName);
        builder.Services.Configure<SchedulerIdentityOptions>(section);
        var options = section.Get<SchedulerIdentityOptions>() ?? new();
        var development = builder.Environment.IsDevelopment();

        var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(cookie =>
            {
                cookie.LoginPath = ExternalSignIn.SignInPath;
                cookie.LogoutPath = ExternalSignIn.SignOutPath;
                cookie.Cookie.Name = "scheduler.auth";
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
                cookie.SlidingExpiration = true;
            })
            // Serves /.well-known/oauth-protected-resource (RFC 9728) and writes the 401 challenge that
            // points MCP clients at it.
            .AddMcp(mcp => mcp.Events.OnResourceMetadataRequest = context =>
            {
                context.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = McpResource.Canonical(context.Request),
                    AuthorizationServers = [McpResource.Issuer(context.Request)],
                    ScopesSupported = [McpResource.Scope],
                    ResourceName = "Scheduler",
                };
                return Task.CompletedTask;
            })
            .AddPolicyScheme(McpBearerScheme, "MCP bearer token", scheme =>
            {
                scheme.ForwardDefaultSelector = http => PersonalAccessTokenHandler.IsPersonalAccessToken(http)
                    ? PersonalAccessTokenHandler.SchemeName
                    : OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
                scheme.ForwardChallenge = McpAuthenticationDefaults.AuthenticationScheme;
                scheme.ForwardForbid = McpAuthenticationDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenHandler>(PersonalAccessTokenHandler.SchemeName, null);
        if (development)
            authentication.AddScheme<AuthenticationSchemeOptions, DevEmailHeader>(DevEmailHeader.SchemeName, null);

        builder.Services.AddAuthorization(authorization => authorization.AddPolicy(McpPolicy, policy =>
        {
            policy.AuthenticationSchemes.Add(McpBearerScheme);
            if (development)
                policy.AuthenticationSchemes.Add(DevEmailHeader.SchemeName);
            policy.RequireAuthenticatedUser();
        }));

        var openIddict = builder.Services.AddOpenIddict()
            // OpenIddict's stores take the scoped context that AddDbContextFactory also registers. They run
            // per HTTP request on the OAuth endpoints, never inside a Blazor circuit.
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<SchedulerDbContext>())
            .AddServer(server =>
            {
                server.SetAuthorizationEndpointUris(AuthorizationEndpoint.Path.TrimStart('/'))
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");

                server.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange();
                server.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess, McpResource.Scope);

                // The resource is this server's own /mcp URI, which comes from the request, so it can't be
                // registered up front; AuthorizationEndpoint refuses any other resource instead.
                server.DisableResourceValidation()
                    .IgnoreResourcePermissions();

                server.SetAccessTokenLifetime(AccessTokenLifetime)
                    .SetRefreshTokenLifetime(RefreshTokenLifetime);

                AddCertificates(server, options, development);

                var aspNetCore = server.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                if (development)
                    aspNetCore.DisableTransportSecurityRequirement();

                server.AddEventHandler(ResolveClientMetadataDocument.Descriptor)
                    .AddEventHandler(AttachClientRegistrationMetadata.Descriptor);
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
                validation.AddEventHandler(RequireMcpAudience.Descriptor);
            });

        if (options.ConfiguredProviders.Count > 0)
            openIddict.AddClient(client => AddExternalProviders(client, options, development));

        builder.Services.AddScoped<ClientMetadataDocuments>();
        builder.Services.AddHttpClient(ClientMetadataDocuments.HttpClientName, http =>
            {
                http.Timeout = TimeSpan.FromSeconds(10);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Scheduler (client metadata fetch)");
            })
            // The timeout bounds the whole fetch, retries included: a document that can't be fetched
            // promptly fails the sign-in.
            .ConfigurePrimaryHttpMessageHandler(ClientMetadataDocuments.CreateHandler);

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(DynamicClientRegistration.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));
        });
    }

    public static void MapSchedulerIdentity(this WebApplication app)
    {
        app.MapExternalSignIn();
        app.MapAuthorizationEndpoint();
        app.MapDynamicClientRegistration();
        app.MapDevSignIn();
    }

    private static void AddExternalProviders(OpenIddictClientBuilder client, SchedulerIdentityOptions options, bool development)
    {
        client.AllowAuthorizationCodeFlow();
        AddCertificates(client, options, development);

        var aspNetCore = client.UseAspNetCore().EnableRedirectionEndpointPassthrough();
        if (development)
            aspNetCore.DisableTransportSecurityRequirement();
        client.UseSystemNetHttp().SetProductInformation(typeof(IdentitySetup).Assembly);

        var providers = client.UseWebProviders();
        if (options.Microsoft.IsConfigured)
            providers.AddMicrosoft(microsoft => microsoft
                .SetClientId(options.Microsoft.ClientId!)
                .SetClientSecret(options.Microsoft.ClientSecret!)
                .SetRedirectUri(ExternalSignIn.CallbackPath + "microsoft")
                .AddScopes(Scopes.Email, Scopes.Profile));
        if (options.Google.IsConfigured)
            providers.AddGoogle(google => google
                .SetClientId(options.Google.ClientId!)
                .SetClientSecret(options.Google.ClientSecret!)
                .SetRedirectUri(ExternalSignIn.CallbackPath + "google")
                .AddScopes(Scopes.Email, Scopes.Profile));
    }

    private static void AddCertificates(OpenIddictServerBuilder server, SchedulerIdentityOptions options, bool development)
    {
        if (LoadCertificates(options, development) is var (signing, encryption))
            server.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
        else
            server.AddDevelopmentSigningCertificate().AddDevelopmentEncryptionCertificate();
    }

    private static void AddCertificates(OpenIddictClientBuilder client, SchedulerIdentityOptions options, bool development)
    {
        if (LoadCertificates(options, development) is var (signing, encryption))
            client.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
        else
            client.AddDevelopmentSigningCertificate().AddDevelopmentEncryptionCertificate();
    }

    /// <summary>The configured certificates, or null in Development to use development certificates.</summary>
    private static (X509Certificate2 Signing, X509Certificate2 Encryption)? LoadCertificates(SchedulerIdentityOptions options, bool development)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningCertificate) && !string.IsNullOrWhiteSpace(options.EncryptionCertificate))
            return (Load(options.SigningCertificate), Load(options.EncryptionCertificate));
        if (development)
            return null;
        throw new InvalidOperationException(
            "Identity:SigningCertificate and Identity:EncryptionCertificate are not configured. Set each to a base64 PKCS#12 certificate with a private key.");

        static X509Certificate2 Load(string base64) =>
            X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), password: null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
