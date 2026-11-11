using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
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
/// check passed: an HMAC over the authorize request (scope and PKCE method included), the owner id, this browser's
/// consent cookie, a nonce and an expiry, keyed by a secret every replica derives from the signing key. It grants once:
/// Allow spends its nonce in the database. A query string never grants, and a cross-site post cannot know the unlock,
/// so a crafted link or form lands on the page. A failed check gets the page again (400), never a redirect to the
/// client.</para>
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
            McpOAuthClients.IsLoopback(request.RedirectUri),
            clients.IsMetadataDocumentId(request.ClientId, out var doc) ? doc : null,
            ReturnUrl(issuer, request),
            clock.GetUtcNow());
        var page = new ConsentPage(options, consentRequest, OAuthHidden(request));

        var form = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.HasFormContentType ? await ctx.Request.ReadFormAsync(ct) : null;
        var consent = form?["consent"].ToString() ?? "";
        if (consent == "deny") return OAuthError(Errors.AccessDenied, "The owner denied this connection.");

        var owner = await services.GetRequiredService<IMcpConsentOwner>().CheckAsync(consentRequest, ctx, form, ct);
        var unlockGiven = form?["unlock"].ToString() ?? "";
        var browser = ctx.Request.Cookies[BrowserCookie(issuer)];
        var unlocked = unlockGiven.Length > 0 ? VerifyUnlock(keys.ConsentKey, request, unlockGiven, browser, consentRequest.Now) : null;
        var expired = browser is null ? "This page needs its cookie: allow cookies for this site, then confirm again." : "This page expired. Confirm again.";
        if (owner is McpOwnerCheck.Redirect signIn) return Results.Redirect(signIn.Url);

        // The owner as this request proves them: a session the check recognises now, or a credential proven on an earlier
        // step of this same authorize request in this same browser (the unlock). A session that changed owner since then
        // wins over the unlock.
        var ownerId = owner is McpOwnerCheck.Proven p ? p.OwnerId : unlocked?.OwnerId;
        if (ownerId is null)
        {
            var credential = (McpOwnerCheck.Credential)owner;
            var error = credential.Error
                        ?? (unlockGiven.Length > 0 ? expired
                            : consent.Length > 0 ? "Confirm that you are the owner first." : null);
            return page.Render(ctx, ConsentPage.CredentialStep(credential.FieldsHtml), error, consent.Length > 0 ? 400 : 200);
        }

        var store = services.GetRequiredService<IMcpConnectionStore>();
        var unlock = MintUnlock(keys.ConsentKey, request, ownerId, BindBrowser(ctx, issuer, browser), consentRequest.Now + options.ConsentLifetime);
        if (consent != "allow")
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, null, ct), unlock), null, 200);
        if (unlocked is null || unlocked.OwnerId != ownerId)
        {
            // Allow without this page's own proof (a forged or cross-site post, another browser, or the owner changed):
            // show the choices again, never a grant.
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, form, ct), unlock), expired, 400);
        }
        if (!await ConsumeAsync(services, keys.ConsentKey, unlocked, ct))
        {
            // The same unlock again: a double submit, a replay, or another replica already granted with it.
            return page.Render(ctx, ConsentPage.ChoicesStep(await store.RenderChoicesAsync(consentRequest, form, ct), unlock),
                "This page was already used. Confirm again to make another connection.", 400);
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

    // ---------------------------------------------------------------------------------------------- the unlock
    //
    // "2.<expires>.<nonce>.<owner id>.<mac>", the last three base64url. The MAC covers, length-prefixed, the expiry, the
    // nonce, the owner id, this browser's consent cookie and every authorize parameter the grant depends on. Allow spends
    // it once: its nonce is claimed in the token table's unique ReferenceId index, so a second use fails on every replica.

    /// <summary>What a valid unlock proves.</summary>
    private sealed record Unlocked(string OwnerId, string Nonce, DateTimeOffset Expires);

    private const string UnlockVersion = "2";

    /// <summary>The token type of a spent unlock's row. Never a token anyone holds: OpenIddict never reads these rows.</summary>
    private const string SpentUnlockType = "urn:agentkit:mcp:spent-consent-unlock";

    /// <summary>
    /// The browser-binding cookie: random, HttpOnly, SameSite=Strict, a session cookie. <c>__Host-</c> (Secure, Path=/)
    /// on an https issuer; a plain name on an http (development) one, where a browser would drop a Secure cookie.
    /// </summary>
    private static string BrowserCookie(McpOAuthIssuer issuer) => issuer.AllowsHttp ? "agentkit-mcp-consent" : "__Host-agentkit-mcp-consent";

    /// <summary>This browser's binding: the cookie it sent when well formed, otherwise a new one set on this response.</summary>
    private static string BindBrowser(HttpContext ctx, McpOAuthIssuer issuer, string? sent)
    {
        if (sent is { Length: 43 } && sent.AsSpan().IndexOfAnyExcept(Base64UrlChars) < 0) return sent;
        var fresh = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        ctx.Response.Cookies.Append(BrowserCookie(issuer), fresh, new CookieOptions
        {
            HttpOnly = true,
            Secure = !issuer.AllowsHttp,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        return fresh;
    }

    private static readonly System.Buffers.SearchValues<char> Base64UrlChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static string MintUnlock(byte[] key, OpenIddictRequest request, string ownerId, string browser, DateTimeOffset expires)
    {
        var at = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
        return string.Join('.', UnlockVersion, at, nonce, Base64UrlEncoder.Encode(ownerId),
            Base64UrlEncoder.Encode(Mac(key, request, at, nonce, ownerId, browser)));
    }

    /// <summary>What an unlock proves, or null when it is malformed, forged, for another request or browser, or expired.</summary>
    private static Unlocked? VerifyUnlock(byte[] key, OpenIddictRequest request, string unlock, string? browser, DateTimeOffset now)
    {
        var parts = unlock.Split('.');
        if (browser is null || parts.Length != 5 || parts[0] != UnlockVersion
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires > DateTimeOffset.MaxValue.ToUnixTimeSeconds() || DateTimeOffset.FromUnixTimeSeconds(expires) < now)
            return null;
        try
        {
            var ownerId = Base64UrlEncoder.Decode(parts[3]);
            var given = Base64UrlEncoder.DecodeBytes(parts[4]);
            return CryptographicOperations.FixedTimeEquals(given, Mac(key, request, parts[1], parts[2], ownerId, browser))
                ? new Unlocked(ownerId, parts[2], DateTimeOffset.FromUnixTimeSeconds(expires))
                : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The MAC over an unambiguous encoding: each field as a 4-byte big-endian length (−1 for absent) and its UTF-8
    /// bytes, so no attacker-chosen value (<c>state</c>, say) can shift a boundary.
    /// </summary>
    private static byte[] Mac(byte[] key, OpenIddictRequest request, string expires, string nonce, string ownerId, string browser)
    {
        var resources = request.GetResources().ToList();
        string?[] fields =
        [
            "agentkit-mcp-consent-v2", expires, nonce, ownerId, browser,
            request.ClientId, request.RedirectUri, request.ResponseType, request.Scope, request.State,
            request.CodeChallenge, request.CodeChallengeMethod, resources.Count.ToString(CultureInfo.InvariantCulture),
            .. resources,
        ];
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = field is null ? null : Encoding.UTF8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            buffer.Write(length);
            if (bytes is not null) buffer.Write(bytes);
        }
        return HMACSHA256.HashData(key, buffer.ToArray());
    }

    /// <summary>
    /// Spends an unlock: true the first time on any replica, false ever after. A row in OpenIddict's token table keyed by
    /// a value derived from the nonce (never the nonce itself, so nothing a client holds finds it), claimed through the
    /// unique ReferenceId index: no schema change, and atomic under concurrent submits.
    /// </summary>
    private static async Task<bool> ConsumeAsync(IServiceProvider services, byte[] key, Unlocked unlocked, CancellationToken ct)
    {
        var referenceId = "agentkit-consent:" + Base64UrlEncoder.Encode(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("agentkit-mcp-consent-spent\n" + unlocked.Nonce)));
        var db = services.GetRequiredService<McpOAuthDbContext>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var id = Guid.CreateVersion7().ToString();
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "OpenIddictTokens" ("Id", "ReferenceId", "Type", "Status", "CreationDate", "ExpirationDate", "RedemptionDate")
            VALUES ({id}, {referenceId}, {SpentUnlockType}, {OpenIddictConstants.Statuses.Redeemed}, {now}, {unlocked.Expires}, {now})
            ON CONFLICT ("ReferenceId") DO NOTHING
            """, ct);
        return claimed == 1;
    }
}
