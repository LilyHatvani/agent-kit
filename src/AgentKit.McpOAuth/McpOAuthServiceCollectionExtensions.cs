using AgentKit.McpOAuth.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace AgentKit.McpOAuth;

/// <summary>Registers the MCP authorization server.</summary>
public static class McpOAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OAuth 2.1 authorization server for <c>/mcp</c>: OpenIddict with its EF Core stores on PostgreSQL,
    /// authorization code + PKCE S256 only, public clients, opaque reference access and refresh tokens stored by hash,
    /// strict refresh rotation (a reused refresh token revokes the grant), audience pinned to <c>&lt;issuer&gt;/mcp</c>,
    /// RFC 8707 resource indicators, RFC 9207 <c>iss</c> on every authorization response, and token validation that reads
    /// the token and its grant on every call.
    /// </summary>
    /// <remarks>
    /// The host also registers an <see cref="IMcpConnectionStore"/> and an <see cref="IMcpConsentOwner"/>, runs
    /// <see cref="McpOAuthSchema.ReadScript"/> with its own migrator, and wires the middleware (see
    /// <see cref="McpOAuthApplicationBuilderExtensions"/>). With <see cref="McpOAuthOptions.PublicUrl"/> unset only the
    /// stores and <see cref="IMcpOAuthGrants"/> are registered.
    /// </remarks>
    public static IServiceCollection AddMcpOAuth(this IServiceCollection services, Action<McpOAuthOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new McpOAuthOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<McpOAuthDbContext>(o =>
        {
            o.UseNpgsql(options.ConnectionString);
            o.UseOpenIddict();
        });
        services.AddSingleton<IMcpOAuthGrants, McpOAuthGrants>();
        // OpenIddict logs an authorization request's parameters at Information, and a consent POST can carry an owner
        // secret: keep its category at Warning unless the host's configuration names it more specifically.
        services.AddLogging(b => b.AddFilter("OpenIddict", LogLevel.Warning));
        // UseAuthentication must work with the server off too (the gate then serves only the host's own bearers).
        services.AddAuthentication();

        var openIddict = services.AddOpenIddict()
            .AddCore(o =>
            {
                o.UseEntityFrameworkCore().UseDbContext<McpOAuthDbContext>();
                o.ReplaceApplicationManager(typeof(LoopbackApplicationManager<>));
            });

        if (string.IsNullOrWhiteSpace(options.PublicUrl)) return services;
        var issuer = McpOAuthIssuer.Parse(options.PublicUrl);
        services.AddSingleton(issuer);
        services.AddSingleton<McpOAuthClients>();
        services.AddSingleton<McpOAuthKeys>();
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>>(sp => sp.GetRequiredService<McpOAuthKeys>());
        services.AddHttpClient(McpOAuthDefaults.MetadataHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHostedService<McpOAuthSeeder>();

        openIddict
            .AddServer(o =>
            {
                o.SetIssuer(issuer.Issuer);
                o.SetAuthorizationEndpointUris(McpOAuthDefaults.AuthorizePath.TrimStart('/'))
                    .SetTokenEndpointUris(McpOAuthDefaults.TokenPath.TrimStart('/'))
                    .SetConfigurationEndpointUris(".well-known/oauth-authorization-server", ".well-known/openid-configuration")
                    .SetJsonWebKeySetEndpointUris(".well-known/jwks");

                o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
                o.UseReferenceAccessTokens().UseReferenceRefreshTokens();
                o.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
                o.SetAccessTokenLifetime(options.AccessTokenLifetime);
                o.SetRefreshTokenLifetime(options.RefreshTokenLifetime);
                o.SetAuthorizationCodeLifetime(options.AuthorizationCodeLifetime);
                o.RegisterScopes(options.Scope, Scopes.OfflineAccess);

                // There is one resource, checked by the consent page (invalid_target otherwise) and stamped as the
                // audience, so OpenIddict's per-client resource registry would add nothing but refusals.
                o.Configure(server =>
                {
                    server.DisableResourceValidation = true;
                    server.IgnoreResourcePermissions = true;
                    // S256 only: OpenIddict then refuses "plain" itself, and discovery lists S256 alone.
                    server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                });

                // The token endpoint holds the consent page's line: a code or refresh token is never exchanged for a
                // token naming another resource (RFC 8707 invalid_target). With resource validation off, OpenIddict
                // would otherwise ignore the indicator and issue a token for /mcp regardless.
                o.AddEventHandler<ValidateTokenRequestContext>(b => b.UseInlineHandler(ctx =>
                {
                    if (ctx.Request.GetResources().Any(r => r.TrimEnd('/') != issuer.ResourceUrl))
                        ctx.Reject(Errors.InvalidTarget, $"The only resource here is {issuer.ResourceUrl}.");
                    return default;
                }));

                // Discovery as MCP clients read it: public clients only ("none"), and client id metadata documents, which
                // OpenIddict does not know about. Claude and ChatGPT pick metadata documents only when "none" is listed,
                // and a listed private_key_jwt would invite a client to sign with a key no client here has.
                o.AddEventHandler<ApplyConfigurationResponseContext>(b => b.UseInlineHandler(ctx =>
                {
                    if (ctx.Response.Error is not null) return default;
                    ctx.Response[Metadata.TokenEndpointAuthMethodsSupported] = new System.Text.Json.Nodes.JsonArray(ClientAuthenticationMethods.None);
                    ctx.Response["client_id_metadata_document_supported"] = options.MetadataDocumentHosts.Count > 0;
                    return default;
                }).SetOrder(int.MinValue + 100_000));

                var aspNet = o.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
                if (issuer.AllowsHttp) aspNet.DisableTransportSecurityRequirement();
            })
            .AddValidation(o =>
            {
                // Same process, same store: every call reads the token row and its grant, so a revoked grant fails the
                // next call rather than at expiry.
                o.UseLocalServer();
                o.UseAspNetCore();
                o.EnableAuthorizationEntryValidation();
                o.AddAudiences(issuer.ResourceUrl);
            });

        return services;
    }
}
