using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Scheduler.Web.Tests;

/// <summary>
/// Outside Development the token certificates come from configuration, as docs/identity-setup.md describes;
/// these tests start the app the way production does, with the documented setting names.
/// </summary>
[TestClass]
public sealed class ProductionConfigurationTests
{
    [TestMethod]
    public async Task Configured_certificates_are_used_and_discovery_is_served_over_https()
    {
        await using var app = new ProductionApp(
            Base64Pfx("CN=Test token signing", X509KeyUsageFlags.DigitalSignature),
            Base64Pfx("CN=Test token encryption", X509KeyUsageFlags.KeyEncipherment));
        var http = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://scheduler.example") });

        var metadata = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server");
        var keys = await http.GetFromJsonAsync<JsonElement>("/.well-known/jwks");

        Assert.AreEqual("https://scheduler.example/", metadata.GetProperty("issuer").GetString());
        Assert.AreEqual(1, keys.GetProperty("keys").GetArrayLength(), "the configured signing key, and only it");
    }

    [TestMethod]
    public void Missing_certificates_stop_the_app_from_starting()
    {
        using var app = new ProductionApp(signingCertificate: null, encryptionCertificate: null);

        var ex = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        StringAssert.Contains(ex.Message, "Identity:SigningCertificate");
    }

    /// <summary>A self-signed certificate with the one key usage OpenIddict requires, as base64 PKCS#12 without a password.</summary>
    private static string Base64Pfx(string subject, X509KeyUsageFlags usage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
    }

    private sealed class ProductionApp(string? signingCertificate, string? encryptionCertificate) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:schedulerdb", PostgresContainer.NewDatabase());
            builder.UseSetting("Scheduler:MagicLinkKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            if (signingCertificate is not null)
                builder.UseSetting("Identity:SigningCertificate", signingCertificate);
            if (encryptionCertificate is not null)
                builder.UseSetting("Identity:EncryptionCertificate", encryptionCertificate);
        }
    }
}
