using System.Security.Claims;
using Adjutant.Core.Models;
using Adjutant.Core.Security;
using Adjutant.HttpServer.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Adjutant.HttpServer.Security;

internal sealed class HttpMcpCallerContextAccessor(
    IHttpContextAccessor http,
    ILogger<HttpMcpCallerContextAccessor> logger) : IMcpCallerContextAccessor
{
    private readonly AsyncLocal<McpCallerContext?> _activeCaller = new();
    private static readonly McpCallerContext Anonymous = new(null, null, []);

    public McpCallerContext Current =>
        _activeCaller.Value ?? GetCaller(http.HttpContext?.User) ?? Anonymous;

    public async ValueTask<CallToolResult> InvokeToolAsync(
        RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        bool requireCredential,
        CancellationToken cancellationToken)
    {
        // The transport attaches the validated principal to the message. Keep its scope with the
        // tool invocation even when the SDK dispatches the call outside the HTTP request.
        var caller = GetCaller(request.User) ?? GetCaller(http.HttpContext?.User);
        if (caller is null && requireCredential)
            throw new McpException("An authenticated MCP credential is required.");

        var previous = _activeCaller.Value;
        _activeCaller.Value = caller ?? Anonymous;
        try
        {
            logger.LogInformation("MCP tool {Tool} called by {UserId} with credential {CredentialId}",
                request.Params?.Name, caller?.UserId ?? (caller is null ? "anonymous" : "API key"), caller?.CredentialId);
            return await next(request, cancellationToken);
        }
        finally
        {
            _activeCaller.Value = previous;
        }
    }

    private static McpCallerContext? GetCaller(ClaimsPrincipal? principal)
    {
        if (principal?.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
            return null;

        var allowedAccounts = identity.FindAll(McpAccountSelection.ClaimType).Select(c => c.Value).ToArray();
        if (identity.AuthenticationType == McpApiKeyHandler.SchemeName
            && identity.FindFirst(McpApiKeyHandler.KeyIdClaimType)?.Value is { } keyId)
            return new McpCallerContext(identity.FindFirst(McpApiKeyHandler.OwnerClaimType)?.Value,
                keyId, allowedAccounts,
                identity.FindFirst(McpApiKeyHandler.OrganizationClaimType)?.Value ?? AccountInfo.DefaultOrganizationId);

        if (identity.FindFirst(Claims.Subject)?.Value is { } subject
            && identity.GetAuthorizationId() is { } authorizationId)
            return new McpCallerContext(subject == AuthorizationEndpoint.AdminTokenSubject ? null : subject,
                authorizationId, allowedAccounts);

        return null;
    }
}
