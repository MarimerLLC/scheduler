using Microsoft.AspNetCore;
using OpenIddict.Server;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Scheduler.Web.Identity;

/// <summary>
/// Before OpenIddict looks up the client, a URL client_id is resolved through its metadata document, so the
/// built-in client, redirect URI and PKCE checks then run against what the document says.
/// </summary>
internal sealed class ResolveClientMetadataDocument(ClientMetadataDocuments documents) : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessAuthenticationContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessAuthenticationContext>()
            .UseScopedHandler<ResolveClientMetadataDocument>()
            .SetOrder(OpenIddictServerHandlers.ValidateClientId.Descriptor.Order - 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessAuthenticationContext context)
    {
        if (!ClientMetadataDocuments.IsMetadataDocumentClientId(context.ClientId))
            return;

        if (await documents.EnsureRegisteredAsync(context.ClientId!, context.CancellationToken) is { } error)
            context.Reject(
                error: context.EndpointType == OpenIddictServerEndpointType.Authorization ? Errors.InvalidRequest : Errors.InvalidClient,
                description: error);
    }
}

/// <summary>
/// Discovery: says that URL client_ids (metadata documents) are accepted, and where dynamic client
/// registration is, neither of which OpenIddict has built in.
/// </summary>
internal sealed class AttachClientRegistrationMetadata : IOpenIddictServerHandler<OpenIddictServerEvents.HandleConfigurationRequestContext>
{
    public const string RegistrationPath = "connect/register";

    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.HandleConfigurationRequestContext>()
            .UseSingletonHandler<AttachClientRegistrationMetadata>()
            .SetOrder(OpenIddictServerHandlers.Discovery.AttachAdditionalMetadata.Descriptor.Order + 1_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(OpenIddictServerEvents.HandleConfigurationRequestContext context)
    {
        context.Metadata["client_id_metadata_document_supported"] = true;
        if (context.Issuer is { } issuer)
            context.Metadata["registration_endpoint"] = new Uri(issuer, RegistrationPath).AbsoluteUri;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An access token is only good at <c>/mcp</c> if it was issued for this server's canonical MCP URI (RFC 8707
/// audience). That URI comes from the request, so it is added here rather than configured once.
/// </summary>
internal sealed class RequireMcpAudience : IOpenIddictValidationHandler<OpenIddictValidationEvents.ValidateTokenContext>
{
    public static OpenIddictValidationHandlerDescriptor Descriptor { get; } =
        OpenIddictValidationHandlerDescriptor.CreateBuilder<OpenIddictValidationEvents.ValidateTokenContext>()
            .UseSingletonHandler<RequireMcpAudience>()
            .SetOrder(OpenIddictValidationHandlers.Protection.ValidateAudiences.Descriptor.Order - 500)
            .SetType(OpenIddictValidationHandlerType.Custom)
            .Build();

    public ValueTask HandleAsync(OpenIddictValidationEvents.ValidateTokenContext context)
    {
        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The ASP.NET Core request isn't available.");
        context.ValidAudiences.Add(McpResource.Canonical(request));
        return ValueTask.CompletedTask;
    }
}
