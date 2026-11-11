using System.Security.Claims;
using AgentKit.McpOAuth.Internal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentKit.McpOAuth;

/// <summary>
/// The middleware and endpoints, in this order:
/// <code>
/// app.UseMcpOAuth();            // issuer pinning and metadata documents, before anything reads the request
/// app.UseAuthentication();      // OpenIddict's endpoints run here
/// app.UseMcpOAuthGate();        // /mcp: bearer → connection, or 401 with resource_metadata
/// app.UseAuthorization();
/// app.MapMcpOAuth();            // discovery and the consent page
/// </code>
/// Each is a no-op for the OAuth parts when <see cref="McpOAuthOptions.PublicUrl"/> is unset.
/// </summary>
public static class McpOAuthApplicationBuilderExtensions
{
    private static readonly string[] AuthorizeMethods = [HttpMethods.Get, HttpMethods.Post];
    private static readonly string[] HeaderOnly = ["header"];

    /// <summary>
    /// Pins scheme, host and path base to the issuer on <c>/.well-known</c>, <c>/oauth</c> and <c>/mcp</c>. OpenIddict
    /// builds discovery URLs from the request, and behind a TLS-terminating proxy the request is plain http on an internal
    /// host: unpinned, discovery advertises the wrong origin and OpenIddict refuses http outright. The configured origin
    /// wins, whatever arrived; no X-Forwarded-* header is trusted. TLS stays the proxy's job.
    /// On <c>/oauth/authorize</c> (any case, trailing slash or not, GET and POST) it then applies
    /// <see cref="McpOAuthOptions.AuthorizeRateLimiter"/> (429), refuses a client id written as a URL but not canonically
    /// (400), fetches a client id metadata document (cached) before OpenIddict validates the authorize request that
    /// names it, and logs an unknown client at Warning (OpenIddict's own refusal logs below that).
    /// </summary>
    public static IApplicationBuilder UseMcpOAuth(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<McpOAuthIssuer>() is not { } issuer) return app;
        var host = HostString.FromUriComponent(issuer.Issuer);
        var pathBase = new PathString(issuer.Issuer.AbsolutePath.TrimEnd('/'));
        app.Use((ctx, next) =>
        {
            var p = ctx.Request.Path;
            if (p.StartsWithSegments("/.well-known") || p.StartsWithSegments("/oauth") || p.StartsWithSegments(McpOAuthDefaults.ResourcePath))
            {
                ctx.Request.Scheme = issuer.Issuer.Scheme;
                ctx.Request.Host = host;
                ctx.Request.PathBase = pathBase;
            }
            return next(ctx);
        });

        var clients = app.ApplicationServices.GetRequiredService<McpOAuthClients>();
        var limiter = app.ApplicationServices.GetRequiredService<McpOAuthOptions>().AuthorizeRateLimiter;
        var log = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("AgentKit.McpOAuth");
        app.Use(async (ctx, next) =>
        {
            // Routing also answers the path with a trailing slash, and in any case.
            if (!(ctx.Request.Path.Value ?? "").TrimEnd('/').Equals(McpOAuthDefaults.AuthorizePath, StringComparison.OrdinalIgnoreCase))
            {
                await next(ctx);
                return;
            }

            using var lease = limiter is null ? null : await limiter.AcquireAsync(ctx, 1, ctx.RequestAborted);
            if (lease is { IsAcquired: false })
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                if (lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
                    ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
                {
                    ["error"] = Errors.TemporarilyUnavailable,
                    ["error_description"] = "Too many authorization requests. Try again later.",
                }, ctx.RequestAborted);
                return;
            }

            var form = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ctx.RequestAborted) : null;
            var given = form is not null && form.ContainsKey(Parameters.ClientId) ? form[Parameters.ClientId] : ctx.Request.Query[Parameters.ClientId];
            if (given.ToString() is { Length: > 0 } anyClientId && McpOAuthClients.IsNonCanonicalUrlClientId(anyClientId))
            {
                // Never fetched or stored under an alias now, but a row an earlier version stored that way must not be used.
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
                {
                    ["error"] = Errors.InvalidClient,
                    ["error_description"] = "A client_id URL must be written in its canonical form.",
                }, ctx.RequestAborted);
                return;
            }

            if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Query[Parameters.ClientId].ToString() is { Length: > 0 } clientId)
            {
                var apps = ctx.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
                if (clients.IsMetadataDocumentId(clientId, out _))
                    await clients.EnsureMetadataClientAsync(apps, clientId, ctx.RequestAborted);
                if (await apps.FindByClientIdAsync(clientId, ctx.RequestAborted) is null)
                    log.LogWarning("OAuth authorize refused: unknown client_id {ClientId} (seeded: {Seeded}; metadata documents from {Hosts}).",
                        clientId.Length > 200 ? clientId[..200] : clientId, string.Join(", ", clients.Seeded), string.Join(", ", clients.MetadataHosts));
            }
            await next(ctx);
        });
        return app;
    }

    /// <summary>
    /// The per-call gate on <c>/mcp</c>. Every request's bearer goes first to
    /// <see cref="IMcpConnectionStore.ResolveOtherBearerAsync"/>, then to OpenIddict (when the server is on) and
    /// <see cref="IMcpConnectionStore.ResolveAsync"/>, uncached. The principal becomes <c>HttpContext.User</c>; anything
    /// else gets <see cref="McpOAuthHttpContextExtensions.WriteMcpOAuthChallengeAsync"/>. Hosts with their own gate use
    /// <see cref="McpOAuthHttpContextExtensions.AuthenticateMcpOAuthAsync"/> and the challenge writer directly.
    /// </summary>
    public static IApplicationBuilder UseMcpOAuthGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments(McpOAuthDefaults.ResourcePath))
            {
                await next(ctx);
                return;
            }

            var header = ctx.Request.Headers.Authorization.ToString();
            var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > "Bearer ".Length;
            ClaimsPrincipal? principal = null;
            if (presented)
            {
                var store = ctx.RequestServices.GetRequiredService<IMcpConnectionStore>();
                principal = await store.ResolveOtherBearerAsync(header["Bearer ".Length..].Trim(), ctx.RequestAborted);
                if (principal is null && await ctx.AuthenticateMcpOAuthAsync() is { } grant)
                    principal = await store.ResolveAsync(grant, ctx.RequestAborted);
            }

            if (principal is null)
            {
                await ctx.WriteMcpOAuthChallengeAsync(presented);
                return;
            }
            ctx.User = principal;
            await next(ctx);
        });
    }

    /// <summary>
    /// Maps the RFC 9728 protected resource metadata (at <c>/.well-known/oauth-protected-resource</c> and its
    /// <c>/mcp</c>-suffixed form, which clients try first) and the consent page at <c>/oauth/authorize</c> (GET and POST).
    /// OpenIddict serves the token endpoint, the authorization server metadata and the JWKS itself.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpOAuth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetService<McpOAuthIssuer>() is not { } issuer) return endpoints;
        var options = endpoints.ServiceProvider.GetRequiredService<McpOAuthOptions>();

        var metadata = new Dictionary<string, object>
        {
            ["resource"] = issuer.ResourceUrl,
            ["authorization_servers"] = new[] { issuer.Issuer.AbsoluteUri },
            ["scopes_supported"] = new[] { options.Scope },
            ["bearer_methods_supported"] = HeaderOnly,
            ["resource_name"] = options.ResourceName,
        };
        IResult Metadata() => Results.Json(metadata);
        endpoints.MapGet("/.well-known/oauth-protected-resource", Metadata);
        endpoints.MapGet("/.well-known/oauth-protected-resource" + McpOAuthDefaults.ResourcePath, Metadata);
        endpoints.MapMethods(McpOAuthDefaults.AuthorizePath, AuthorizeMethods, (Delegate)ConsentEndpoint.HandleAsync);
        return endpoints;
    }
}

/// <summary>The gate's two halves, for a host that writes its own <c>/mcp</c> gate.</summary>
public static class McpOAuthHttpContextExtensions
{
    /// <summary>
    /// Validates the request's bearer as an OAuth access token from this server: live, unexpired, audience
    /// <c>&lt;issuer&gt;/mcp</c>, grant not revoked, all read from the store now. Null when the server is off or the token
    /// fails any of that. The host must still check its own connection (<see cref="McpOAuthGrant.ConnectionId"/>).
    /// Run after <c>UseAuthentication</c>.
    /// </summary>
    public static async Task<McpOAuthGrant?> AuthenticateMcpOAuthAsync(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RequestServices.GetService<McpOAuthIssuer>() is null) return null;
        var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        if (!result.Succeeded) return null;
        var principal = result.Principal;
        var subject = principal.GetClaim(Claims.Subject);
        var authorization = principal.GetAuthorizationId();
        var client = principal.GetClaim(Claims.ClientId) ?? principal.GetPresenters().FirstOrDefault();
        return Guid.TryParse(subject, out var connectionId) && authorization is not null && client is not null
            ? new McpOAuthGrant(connectionId, authorization, client)
            : null;
    }

    /// <summary>
    /// Writes the 401 for <c>/mcp</c>: <c>WWW-Authenticate: Bearer</c> with <c>error="invalid_token"</c> when a credential
    /// was presented, and <c>resource_metadata</c> (RFC 9728) when the server is on, which is how MCP clients find the
    /// authorization server and know to refresh. The body is <c>{"code":"unauthenticated","message":…}</c>.
    /// </summary>
    public static async Task WriteMcpOAuthChallengeAsync(this HttpContext context, bool credentialPresented)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var challenge = "Bearer realm=\"mcp\"" + (credentialPresented ? ", error=\"invalid_token\"" : "");
        if (context.RequestServices.GetService<McpOAuthIssuer>() is { } issuer)
            challenge += $", resource_metadata=\"{issuer.ProtectedResourceMetadataUrl}\"";
        context.Response.Headers.WWWAuthenticate = challenge;
        await context.Response.WriteAsJsonAsync(new
        {
            code = "unauthenticated",
            message = "Credential missing, unknown, expired or revoked.",
        }, context.RequestAborted);
    }
}
