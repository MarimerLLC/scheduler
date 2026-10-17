namespace Scheduler.Web.Identity;

/// <summary>
/// The <c>Identity</c> configuration section: the external sign-in providers and the authorization server's
/// keys. A provider without a client id isn't offered. See docs/design.md, "Identity and authentication".
/// </summary>
internal sealed class SchedulerIdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>Entra ID and personal Microsoft accounts (the <c>common</c> tenant).</summary>
    public ExternalProviderOptions Microsoft { get; set; } = new();

    public ExternalProviderOptions Google { get; set; } = new();

    /// <summary>
    /// Base64 PKCS#12 (no password) certificates that sign and encrypt the tokens the server issues. Required
    /// outside Development, where development certificates in the user's store are used instead.
    /// </summary>
    public string? SigningCertificate { get; set; }

    /// <inheritdoc cref="SigningCertificate"/>
    public string? EncryptionCertificate { get; set; }

    /// <summary>The configured providers, by OpenIddict provider name.</summary>
    public IReadOnlyList<string> ConfiguredProviders =>
    [
        .. Microsoft.IsConfigured ? [ExternalEmail.Microsoft] : Array.Empty<string>(),
        .. Google.IsConfigured ? [ExternalEmail.Google] : Array.Empty<string>(),
    ];

    /// <summary>The provider name for a URL segment (<c>microsoft</c>, <c>google</c>), if it is configured.</summary>
    public string? Provider(string segment) =>
        ConfiguredProviders.FirstOrDefault(p => string.Equals(p, segment, StringComparison.OrdinalIgnoreCase));
}

internal sealed class ExternalProviderOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
