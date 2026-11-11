using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentKit.McpOAuth.Internal;

/// <summary>
/// <c>/oauth/authorize</c> after OpenIddict has validated the request (client, redirect, PKCE): the consent page, and
/// the minter of the grant. Two steps: the owner check (<see cref="IMcpConsentOwner"/>), then the host's choices
/// (<see cref="IMcpConnectionStore.RenderChoicesAsync"/>) with Allow and Deny.
///
/// <para>Consent counts only as a POSTed <c>consent=allow</c> carrying the unlock this page minted after the owner
/// check passed: an HMAC over the authorize request, the owner id and an expiry, keyed by a secret every replica
/// derives from the signing key. A query string never grants, and a cross-site post cannot know the unlock, so a crafted
/// link or form lands on the page. A failed check gets the page again (400), never a redirect to the client.</para>
/// </summary>
internal static class ConsentEndpoint
{
    /// <summary>
    /// The authorization request parameters the page carries from step to step and into <see cref="McpConsentRequest.ReturnUrl"/>
    /// (RFC 6749, 7636, 8707, 9207 and OpenID Connect). Everything else in a POST is the page's or the host's own field.
    /// </summary>
    private static readonly HashSet<string> OAuthParameters = new(StringComparer.Ordinal)
    {
        Parameters.ResponseType, Parameters.ClientId, Parameters.RedirectUri, Parameters.Scope, Parameters.State,
        Parameters.CodeChallenge, Parameters.CodeChallengeMethod, Parameters.Resource, Parameters.Nonce, Parameters.Prompt,
        Parameters.ResponseMode, Parameters.MaxAge, Parameters.LoginHint, Parameters.UiLocales, Parameters.Claims,
        Parameters.AcrValues, Parameters.Display, Parameters.IdTokenHint, Parameters.Audience,
    };

    public static async Task<IResult> HandleAsync(HttpContext ctx)
    {
        var services = ctx.RequestServices;
        var options = services.GetRequiredService<McpOAuthOptions>();
        var issuer = services.GetRequiredService<McpOAuthIssuer>();
        var clock = services.GetRequiredService<TimeProvider>();
        var apps = services.GetRequiredService<IOpenIddictApplicationManager>();
        var clients = services.GetRequiredService<McpOAuthClients>();
        var keys = services.GetRequiredService<McpOAuthKeys>();
        var ct = ctx.RequestAborted;

        var request = ctx.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("No OpenIddict request.");

        // One resource only: the MCP endpoint. Anything else asks for a token this server must not mint (RFC 8707).
        foreach (var resource in request.GetResources())
            if (resource.TrimEnd('/') != issuer.ResourceUrl)
                return OAuthError(Errors.InvalidTarget, $"The only resource here is {issuer.ResourceUrl}.");
        if (request.CodeChallengeMethod != CodeChallengeMethods.Sha256)
            return OAuthError(Errors.InvalidRequest, "PKCE with code_challenge_method=S256 is required.");

        var app = await apps.FindByClientIdAsync(request.ClientId!, ct)
                  ?? throw new InvalidOperationException("OpenIddict validated an unknown client.");
        var redirect = new Uri(request.RedirectUri!);
        var consentRequest = new McpConsentRequest(
            request.ClientId!,
            await apps.GetDisplayNameAsync(app, ct) ?? request.ClientId!,
            redirect,
            McpOAuthClients.IsLoopback(redirect),
            clients.IsMetadataDocumentId(request.ClientId, out var doc) ? doc : null,
            ReturnUrl(issuer, request),
            clock.GetUtcNow());
        var page = new ConsentPage(options, consentRequest, OAuthHidden(request));

        var form = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ct) : null;
        var consent = form?["consent"].ToString() ?? "";
        if (consent == "deny") return OAuthError(Errors.AccessDenied, "The owner denied this connection.");

        var owner = await services.GetRequiredService<IMcpConsentOwner>().CheckAsync(consentRequest, ctx, form, ct);
        var unlockGiven = form?["unlock"].ToString() ?? "";
        var unlocked = unlockGiven.Length > 0 ? VerifyUnlock(keys.ConsentKey, request, unlockGiven, consentRequest.Now) : null;
        if (owner is McpOwnerCheck.Redirect signIn) return Results.Redirect(signIn.Url);

        // The owner as this request proves them: a session the check recognises now, or a credential proven on an earlier
        // step of this same authorize request (the unlock). A session that changed owner since then wins over the unlock.
        var ownerId = owner is McpOwnerCheck.Proven p ? p.OwnerId : unlocked;
        if (ownerId is null)
        {
            var credential = (McpOwnerCheck.Credential)owner;
            var error = credential.Error
                        ?? (unlockGiven.Length > 0 ? "This page expired. Confirm again."
                            : consent.Length > 0 ? "Confirm that you are the owner first." : null);
            return page.Render(ctx, ConsentPage.CredentialStep(credential.FieldsHtml), error, consent.Length > 0 ? 400 : 200);
        }

        var store = services.GetRequiredService<IMcpConnectionStore>();
        var unlock = MintUnlock(keys.ConsentKey, request, ownerId, consentRequest.Now + options.ConsentLifetime);
        if (consent != "allow")
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, null, ct), unlock), null, 200);
        if (unlocked != ownerId)
        {
            // Allow without this page's own proof (a forged or cross-site post, or the owner changed): show the choices
            // again, never a grant.
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, form, ct), unlock),
                "This page expired. Confirm again.", 400);
        }

        // Allow. The connection id is the grant's subject, so every access token under it names the host's connection.
        var connectionId = Guid.CreateVersion7();
        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, connectionId.ToString());
        var principal = new ClaimsPrincipal(identity);
        // offline_access always: a connection lasts until revoked, so the client gets a refresh token whether it asked or not.
        principal.SetScopes(options.Scope, Scopes.OfflineAccess);
        principal.SetResources(issuer.ResourceUrl);
        principal.SetDestinations(_ => [Destinations.AccessToken]);

        var authorizations = services.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await authorizations.CreateAsync(principal, connectionId.ToString(), (await apps.GetIdAsync(app, ct))!,
            AuthorizationTypes.Permanent, principal.GetScopes(), ct);
        var authorizationId = (await authorizations.GetIdAsync(authorization, ct))!;
        principal.SetAuthorizationId(authorizationId);

        try
        {
            await store.CreateAsync(consentRequest, new McpOAuthGrant(connectionId, authorizationId, request.ClientId!), ownerId, form!, ct);
        }
        catch (McpConsentRejectedException e)
        {
            await authorizations.TryRevokeAsync(authorization, ct);
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, form, ct), unlock), e.Message, 400);
        }

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult OAuthError(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static List<KeyValuePair<string, string>> OAuthHidden(OpenIddictRequest request)
    {
        var hidden = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in request.GetParameters())
        {
            if (!OAuthParameters.Contains(name)) continue;
            foreach (var v in (Microsoft.Extensions.Primitives.StringValues)value)
                if (v is not null) hidden.Add(new(name, v));
        }
        return hidden;
    }

    private static string ReturnUrl(McpOAuthIssuer issuer, OpenIddictRequest request) =>
        QueryHelpers.AddQueryString(issuer.Base + McpOAuthDefaults.AuthorizePath,
            OAuthHidden(request).Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));

    private static string MintUnlock(byte[] key, OpenIddictRequest request, string ownerId, DateTimeOffset expires)
    {
        var at = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return at + "." + Base64UrlEncoder.Encode(ownerId) + "." + Base64UrlEncoder.Encode(Mac(key, request, ownerId, at));
    }

    /// <summary>The owner id an unlock proves, or null when it is malformed, forged, for another request, or expired.</summary>
    private static string? VerifyUnlock(byte[] key, OpenIddictRequest request, string unlock, DateTimeOffset now)
    {
        var parts = unlock.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires > DateTimeOffset.MaxValue.ToUnixTimeSeconds() || DateTimeOffset.FromUnixTimeSeconds(expires) < now)
            return null;
        try
        {
            var ownerId = Base64UrlEncoder.Decode(parts[1]);
            var given = Base64UrlEncoder.DecodeBytes(parts[2]);
            return CryptographicOperations.FixedTimeEquals(given, Mac(key, request, ownerId, parts[0])) ? ownerId : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] Mac(byte[] key, OpenIddictRequest request, string ownerId, string expires)
    {
        var message = string.Join('\n', "agentkit-mcp-consent", expires, ownerId, request.ClientId, request.RedirectUri,
            request.CodeChallenge, request.State, string.Join(' ', request.GetResources()));
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));
    }
}
