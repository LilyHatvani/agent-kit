using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AgentKit.McpOAuth;

/// <summary>
/// The host's connections: one per consent, each bound to an OpenIddict grant (authorization). Register one
/// implementation (scoped or singleton). The package never stores a connection itself.
/// </summary>
public interface IMcpConnectionStore
{
    /// <summary>
    /// The host's part of the consent page's second step, shown only once the owner is proven: an HTML fragment of form
    /// fields (which agent, which projects, a label…), placed inside the page's form. Every value must be HTML-encoded by
    /// the host. Field names must not be <c>consent</c>, <c>unlock</c> or an OAuth parameter name (<c>scope</c>,
    /// <c>state</c>, <c>resource</c>…), which the page owns.
    /// </summary>
    /// <param name="request">The authorize request being consented to.</param>
    /// <param name="submitted">The form the owner posted, when the page is shown again after <see cref="McpConsentRejectedException"/>; null the first time.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    ValueTask<string> RenderChoicesAsync(McpConsentRequest request, IFormCollection? submitted, CancellationToken cancellationToken);

    /// <summary>
    /// The owner allowed: create the connection behind <paramref name="grant"/>, reading the host's choices from
    /// <paramref name="form"/>. Throw <see cref="McpConsentRejectedException"/> to refuse with a message the page shows
    /// (the grant is then revoked and no code is issued). The connection must be live when this returns: the client
    /// redeems its code within seconds.
    /// </summary>
    /// <param name="request">The authorize request being consented to.</param>
    /// <param name="grant">The new grant: <see cref="McpOAuthGrant.ConnectionId"/> is the id every access token will carry.</param>
    /// <param name="ownerId">Who consented, as <see cref="IMcpConsentOwner"/> identified them.</param>
    /// <param name="form">The posted consent form, including the host's own fields.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    ValueTask CreateAsync(McpConsentRequest request, McpOAuthGrant grant, string ownerId, IFormCollection form, CancellationToken cancellationToken);

    /// <summary>
    /// The per-call gate, after OpenIddict has validated the access token (live, unexpired, this resource as audience,
    /// grant not revoked): the connection's principal, or null to refuse. Must read the host's store on every call, never
    /// a cache, so revoking a connection refuses the very next call. The gate sets the result as <c>HttpContext.User</c>.
    /// </summary>
    ValueTask<ClaimsPrincipal?> ResolveAsync(McpOAuthGrant grant, CancellationToken cancellationToken);

    /// <summary>
    /// The gate's first look at a bearer: a credential the host issues itself (a static token, say). Return its principal,
    /// or null to let the OAuth check run. Called on every <c>/mcp</c> request with a bearer, so return null cheaply for
    /// anything that is not the host's (a prefix check). The default accepts nothing.
    /// </summary>
    ValueTask<ClaimsPrincipal?> ResolveOtherBearerAsync(string bearer, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ClaimsPrincipal?>(null);
}

/// <summary>
/// Who may consent: the consent page's owner check. Register one implementation; <see cref="McpSecretOwnerCheck"/>
/// is a ready one for a shared owner secret.
/// </summary>
/// <remarks>
/// The page asks on every request, GET and POST. Whatever this answers, a grant is created only from a POSTed
/// <c>consent=allow</c> that carries the page's own unlock proof, minted after this check returned
/// <see cref="McpOwnerCheck.Owner"/> for this exact authorize request: a crafted link or a cross-site form post gets
/// the page, never a grant.
/// </remarks>
public interface IMcpConsentOwner
{
    /// <summary>Whether this request comes from the owner.</summary>
    /// <param name="request">The authorize request being consented to (<see cref="McpConsentRequest.ReturnUrl"/> for a sign-in redirect).</param>
    /// <param name="context">The HTTP request (cookies, headers).</param>
    /// <param name="form">The posted form; null on GET.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    ValueTask<McpOwnerCheck> CheckAsync(McpConsentRequest request, HttpContext context, IFormCollection? form, CancellationToken cancellationToken);
}

/// <summary>The answer of <see cref="IMcpConsentOwner.CheckAsync"/>.</summary>
public abstract record McpOwnerCheck
{
    private McpOwnerCheck() { }

    /// <summary>The owner, identified by a stable id that the unlock proof binds and <see cref="IMcpConnectionStore.CreateAsync"/> receives.</summary>
    public static McpOwnerCheck Owner(string ownerId) =>
        string.IsNullOrEmpty(ownerId) ? throw new ArgumentException("An owner id is required.", nameof(ownerId)) : new Proven(ownerId);

    /// <summary>
    /// Not proven yet: show the first step with these credential fields (an HTML fragment, values encoded by the host)
    /// and an optional error (plain text). A credential proves the owner once; the second step then carries the unlock.
    /// </summary>
    public static McpOwnerCheck AskForCredential(string fieldsHtml, string? error = null) => new Credential(fieldsHtml, error);

    /// <summary>Not signed in: redirect there (for example a sign-in page that returns to <see cref="McpConsentRequest.ReturnUrl"/>).</summary>
    public static McpOwnerCheck SignIn(string url) =>
        string.IsNullOrEmpty(url) ? throw new ArgumentException("A sign-in URL is required.", nameof(url)) : new Redirect(url);

    /// <summary>The owner is proven.</summary>
    /// <param name="OwnerId">The owner's stable id.</param>
    public sealed record Proven(string OwnerId) : McpOwnerCheck;

    /// <summary>Ask for a credential.</summary>
    /// <param name="FieldsHtml">The credential fields.</param>
    /// <param name="Error">Shown above the fields, or null.</param>
    public sealed record Credential(string FieldsHtml, string? Error) : McpOwnerCheck;

    /// <summary>Send the browser elsewhere to sign in.</summary>
    /// <param name="Url">Where to.</param>
    public sealed record Redirect(string Url) : McpOwnerCheck;
}

/// <summary>An authorize request as the host sees it on the consent page.</summary>
/// <param name="ClientId">The client's id (a seeded id, or a metadata document URL).</param>
/// <param name="ClientName">The client's display name.</param>
/// <param name="RedirectUri">Where the browser goes after the owner chooses.</param>
/// <param name="RedirectsToLoopback">Whether that is a program on the owner's own computer (an http loopback address).</param>
/// <param name="MetadataDocument">The client's metadata document URL, when it registered with one; null for a seeded client.</param>
/// <param name="ReturnUrl">This authorize request as an absolute GET URL (OAuth parameters only), to come back to after signing in.</param>
/// <param name="Now">The server's time.</param>
public sealed record McpConsentRequest(
    string ClientId,
    string ClientName,
    Uri RedirectUri,
    bool RedirectsToLoopback,
    Uri? MetadataDocument,
    string ReturnUrl,
    DateTimeOffset Now);

/// <summary>A grant: the OpenIddict authorization behind one connection.</summary>
/// <param name="ConnectionId">The connection's id, chosen by the package at consent: the subject of every token under the grant.</param>
/// <param name="AuthorizationId">OpenIddict's authorization id: what <see cref="IMcpOAuthGrants.RevokeAsync"/> takes.</param>
/// <param name="ClientId">The client the grant was made to.</param>
public sealed record McpOAuthGrant(Guid ConnectionId, string AuthorizationId, string ClientId);

/// <summary>
/// Thrown by <see cref="IMcpConnectionStore.CreateAsync"/> to refuse a consent (an unknown agent, too many projects…).
/// The page shows <see cref="Exception.Message"/> again with the owner's choices, answers 400, and issues no code.
/// </summary>
public sealed class McpConsentRejectedException : Exception
{
    /// <summary>Creates the refusal with the message the owner sees.</summary>
    public McpConsentRejectedException(string message) : base(message) { }

    /// <summary>Creates the refusal with the message the owner sees and its cause.</summary>
    public McpConsentRejectedException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Creates a refusal with a generic message.</summary>
    public McpConsentRejectedException() : base("The connection could not be created.") { }
}

/// <summary>
/// Revokes grants. Registered whenever the package is, with or without <see cref="McpOAuthOptions.PublicUrl"/>, so a
/// host's CLI or admin action can revoke the grant behind a connection it has just revoked itself.
/// </summary>
public interface IMcpOAuthGrants
{
    /// <summary>
    /// Revokes the authorization and every token issued under it, so the client can neither call nor refresh its way back
    /// in. Idempotent; an unknown id does nothing. Revoke the host's own connection first: the gate reads it on every call.
    /// </summary>
    Task RevokeAsync(string authorizationId, CancellationToken cancellationToken);
}
