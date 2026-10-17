using System.Net;
using System.Security.Claims;
using Scheduler.Web.Identity;

namespace Scheduler.Web.Tests;

/// <summary>The rules that need no running app: verified email claims, client registration, metadata documents.</summary>
[TestClass]
public sealed class IdentityRulesTests
{
    // ── Verified email from an external sign-in ─────────────────────────────

    [TestMethod]
    public void Google_address_is_used_only_when_verified()
    {
        Assert.AreEqual("pat@gmail.com", ExternalEmail.Read("Google", Principal(("email", "Pat@Gmail.com"), ("email_verified", "true"))).Email);
        Assert.IsNull(ExternalEmail.Read("Google", Principal(("email", "pat@gmail.com"), ("email_verified", "false"))).Email);
        Assert.IsNull(ExternalEmail.Read("Google", Principal(("email", "pat@gmail.com"))).Email);
    }

    [TestMethod]
    public void Personal_microsoft_account_address_is_verified()
    {
        var result = ExternalEmail.Read("Microsoft", Principal(("email", "pat@outlook.com"), ("tid", ExternalEmail.ConsumerTenantId), ("name", "Pat")));

        Assert.AreEqual("pat@outlook.com", result.Email);
        Assert.AreEqual("Pat", result.DisplayName);
    }

    [TestMethod]
    public void Entra_address_needs_the_domain_owner_verified_claim()
    {
        const string tenant = "11111111-2222-3333-4444-555555555555";
        Assert.AreEqual("pat@contoso.com", ExternalEmail.Read("Microsoft", Principal(("email", "pat@contoso.com"), ("tid", tenant), ("xms_edov", "true"))).Email);
        Assert.AreEqual("pat@contoso.com", ExternalEmail.Read("Microsoft", Principal(("email", "pat@contoso.com"), ("tid", tenant), ("xms_edov", "1"))).Email);

        var unverified = ExternalEmail.Read("Microsoft", Principal(("email", "pat@contoso.com"), ("tid", tenant)));
        Assert.IsNull(unverified.Email);
        StringAssert.Contains(unverified.Error, "doesn't confirm");
    }

    [TestMethod]
    public void Sign_in_without_an_address_is_refused()
    {
        var result = ExternalEmail.Read("Google", Principal(("email_verified", "true")));

        Assert.IsNull(result.Email);
        StringAssert.Contains(result.Error, "didn't share an email address");
    }

    // ── Client registration ─────────────────────────────────────────────────

    [TestMethod]
    public void Redirect_uris_must_be_https_or_loopback_http()
    {
        Assert.IsNull(ClientRegistration.ParseRedirectUris(["https://claude.ai/api/mcp/auth_callback", "http://127.0.0.1/callback", "http://localhost:3000/cb", "http://[::1]:5000/cb"]).Error);
        Assert.IsNotNull(ClientRegistration.ParseRedirectUris(["http://example.com/callback"]).Error);
        Assert.IsNotNull(ClientRegistration.ParseRedirectUris(["cursor://anysphere.cursor-retrieval/oauth/callback"]).Error);
        Assert.IsNotNull(ClientRegistration.ParseRedirectUris(["https://example.com/callback#fragment"]).Error);
        Assert.IsNotNull(ClientRegistration.ParseRedirectUris(["/relative"]).Error);
        Assert.IsNotNull(ClientRegistration.ParseRedirectUris([]).Error);
    }

    [TestMethod]
    public void Client_names_are_cleaned_for_display()
    {
        Assert.AreEqual("Claude Code", ClientRegistration.CleanName("  Claude\r\n\tCode  "));
        Assert.IsNull(ClientRegistration.CleanName(" \u0007 "));
        Assert.AreEqual(100, ClientRegistration.CleanName(new string('x', 500))!.Length);
    }

    [TestMethod]
    public void Loopback_redirects_make_a_native_client_so_any_port_matches()
    {
        var native = ClientRegistration.Descriptor("id", "App", [new Uri("http://127.0.0.1/callback")], ClientRegistration.Dynamic);
        var web = ClientRegistration.Descriptor("id", "App", [new Uri("https://app.example/callback")], ClientRegistration.Dynamic);

        Assert.AreEqual("native", native.ApplicationType);
        Assert.AreEqual("web", web.ApplicationType);
    }

    // ── Client ID metadata documents ────────────────────────────────────────

    [TestMethod]
    public void Metadata_document_client_id_urls_follow_the_draft()
    {
        Assert.IsNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example/oauth/client.json"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example/"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://user:pw@app.example/client.json"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example/client.json#x"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example/a/../client.json"));
        Assert.IsNotNull(ClientMetadataDocuments.ValidateClientIdUrl("https://app.example/./client.json"));
    }

    [TestMethod]
    public void Metadata_documents_must_describe_a_public_client_at_their_own_url()
    {
        const string url = "https://app.example/client.json";
        ClientMetadataDocument Document() => new()
        {
            ClientId = url,
            ClientName = "App",
            RedirectUris = ["http://127.0.0.1/callback"],
        };

        Assert.IsNull(ClientMetadataDocuments.ValidateDocument(url, Document()));

        var otherUrl = Document();
        otherUrl.ClientId = "https://app.example/other.json";
        var noName = Document();
        noName.ClientName = " ";
        var secret = Document();
        secret.ClientSecret = "shh";
        var basicAuth = Document();
        basicAuth.TokenEndpointAuthMethod = "client_secret_basic";
        var implicitFlow = Document();
        implicitFlow.GrantTypes = ["implicit"];
        var noRedirects = Document();
        noRedirects.RedirectUris = [];

        foreach (var bad in new[] { otherUrl, noName, secret, basicAuth, implicitFlow, noRedirects })
            Assert.IsNotNull(ClientMetadataDocuments.ValidateDocument(url, bad));
    }

    [TestMethod]
    public void Metadata_documents_are_only_fetched_from_public_addresses()
    {
        foreach (var address in new[] { "8.8.8.8", "1.1.1.1", "2606:4700:4700::1111" })
            Assert.IsTrue(ClientMetadataDocuments.IsPublicAddress(IPAddress.Parse(address)), address);

        foreach (var address in new[]
        {
            "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.169.254", "100.100.100.100",
            "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fd00::1", "::ffff:10.0.0.1", "::ffff:127.0.0.1",
        })
            Assert.IsFalse(ClientMetadataDocuments.IsPublicAddress(IPAddress.Parse(address)), address);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));
}
