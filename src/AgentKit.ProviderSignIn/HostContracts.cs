using Microsoft.AspNetCore.Http;

namespace AgentKit.ProviderSignIn;

/// <summary>The provider names, as they appear in paths, in <see cref="ProviderIdentity.Provider"/> and in the providers list.</summary>
public static class SignInProviders
{
    /// <summary>Google.</summary>
    public const string Google = "google";

    /// <summary>Discord.</summary>
    public const string Discord = "discord";
}

/// <summary>The purposes the package itself uses. A host passes its own (<c>setup</c>, <c>bind</c>…) to <see cref="ProviderSignInFlow.BeginAsync"/>.</summary>
public static class ProviderSignInPurposes
{
    /// <summary>A plain sign-in: what the mapped <c>{provider}/start</c> endpoint begins, the only purpose an anonymous browser can ask for.</summary>
    public const string SignIn = "sign-in";
}

/// <summary>
/// The host's half of a callback: what to do with a verified identity, and with a refusal. Register one implementation
/// (scoped or singleton). This is where the host decides who the identity is: look up the bound
/// (<see cref="ProviderIdentity.Provider"/>, <see cref="ProviderIdentity.AccountId"/>) pair, bind it during setup, start
/// its own session. Never match by <see cref="ProviderIdentity.Email"/>.
/// </summary>
public interface IProviderSignInCompletion
{
    /// <summary>
    /// The provider verified this account for the sign-in the host began with <paramref name="success"/>'s context.
    /// Return the response: typically set the session cookie and redirect to <see cref="ProviderSignInContext.ReturnTo"/>,
    /// or refuse an identity that is not bound.
    /// </summary>
    ValueTask<IResult> SignedInAsync(ProviderSignInSuccess success, HttpContext context, CancellationToken cancellationToken);

    /// <summary>
    /// The sign-in was refused before any identity was verified. Return the response: typically a redirect to the sign-in
    /// page with <see cref="ProviderSignInFailure.Code"/>. The codes are coarse on purpose; the details are logged.
    /// </summary>
    ValueTask<IResult> FailedAsync(ProviderSignInFailure failure, HttpContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What the host attaches to a sign-in when it begins one, kept server-side with the state and handed back at the
/// callback. Never in a URL: the browser cannot change it.
/// </summary>
/// <param name="Purpose">Why this sign-in runs (<see cref="ProviderSignInPurposes.SignIn"/>, or the host's own, such as <c>setup</c> or <c>bind</c>).</param>
/// <param name="Data">Anything else the host needs at the callback (the session that asked to bind, say). Null for none.</param>
/// <param name="ReturnTo">
/// Where to land afterwards: a path on this site (<c>/settings</c>), or an absolute URL on <see cref="ProviderSignInOptions.PublicUrl"/>'s
/// origin, which is kept as its path and query. Anything else (another origin, <c>//host</c>, a backslash) is dropped to null.
/// </param>
public sealed record ProviderSignInContext(string Purpose, string? Data = null, string? ReturnTo = null)
{
    /// <summary>A plain sign-in with nowhere particular to return to.</summary>
    public static ProviderSignInContext SignIn { get; } = new(ProviderSignInPurposes.SignIn);
}

/// <summary>
/// An account the provider vouched for in this sign-in. <see cref="Provider"/> and <see cref="AccountId"/> together
/// identify it; everything else is a display hint the provider's user can change at will.
/// </summary>
/// <param name="Provider"><see cref="SignInProviders.Google"/> or <see cref="SignInProviders.Discord"/>.</param>
/// <param name="AccountId">The provider's stable account id: Google's <c>sub</c>, Discord's user id. Never reassigned by the provider.</param>
/// <param name="DisplayName">The account's name (Google <c>name</c>; Discord <c>global_name</c>, else <c>username</c>). Display only.</param>
/// <param name="Email">The account's email, when the scope asked for it. <b>Display only</b>: emails change and get recycled, so never bind or match by it.</param>
/// <param name="EmailVerified">Whether the provider says it verified <see cref="Email"/>. Still display only.</param>
public sealed record ProviderIdentity(string Provider, string AccountId, string? DisplayName, string? Email, bool EmailVerified);

/// <summary>A sign-in that completed: the verified identity and the context the host began it with.</summary>
/// <param name="Identity">The verified account.</param>
/// <param name="Context">What the host passed to <see cref="ProviderSignInFlow.BeginAsync"/>.</param>
public sealed record ProviderSignInSuccess(ProviderIdentity Identity, ProviderSignInContext Context);

/// <summary>A sign-in that was refused.</summary>
/// <param name="Provider">The provider the callback was for.</param>
/// <param name="Error">Why, coarsely.</param>
/// <param name="Context">The context the host began it with, once the state was read; null when the state itself was refused.</param>
public sealed record ProviderSignInFailure(string Provider, ProviderSignInError Error, ProviderSignInContext? Context)
{
    /// <summary>The error as a short code for a URL (<c>state_invalid</c>, <c>exchange_failed</c>…).</summary>
    public string Code => Error switch
    {
        ProviderSignInError.StateExpired => "state_expired",
        ProviderSignInError.Denied => "denied",
        ProviderSignInError.ExchangeFailed => "exchange_failed",
        ProviderSignInError.IdentityRejected => "identity_rejected",
        ProviderSignInError.ProviderUnavailable => "provider_unavailable",
        _ => "state_invalid",
    };
}

/// <summary>Why a sign-in was refused.</summary>
public enum ProviderSignInError
{
    /// <summary>No state, an unknown or already used one, one from another browser, or one for another provider. Start again.</summary>
    StateInvalid = 0,

    /// <summary>The state was genuine but older than <see cref="ProviderSignInOptions.StateLifetime"/>. Start again.</summary>
    StateExpired,

    /// <summary>The provider sent an error back (the user cancelled, say).</summary>
    Denied,

    /// <summary>The provider refused the code.</summary>
    ExchangeFailed,

    /// <summary>
    /// The provider's answer did not prove an account for this sign-in: an ID token with a bad signature, issuer,
    /// audience, expiry or nonce, or no usable user id.
    /// </summary>
    IdentityRejected,

    /// <summary>The provider did not answer, or answered as a provider in trouble (5xx, 429).</summary>
    ProviderUnavailable,
}

/// <summary>
/// Where sign-ins in flight live between the start and the callback. The package ships an in-memory store and a
/// PostgreSQL one (<see cref="ProviderSignInOptions.ConnectionString"/>); a host may register its own.
/// </summary>
public interface IProviderSignInStateStore
{
    /// <summary>Stores a new sign-in in flight. May sweep rows that expired before <paramref name="now"/>.</summary>
    Task SaveAsync(ProviderSignInState state, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the sign-in stored under <paramref name="key"/> and returns it, or null when there is none. Atomic: of
    /// two concurrent takes of one key, at most one gets the row. Return expired rows too; the caller checks expiry.
    /// </summary>
    Task<ProviderSignInState?> TakeAsync(string key, CancellationToken cancellationToken);
}

/// <summary>One sign-in in flight, as a store keeps it.</summary>
/// <param name="Key">The SHA-256 of the state (base64url), never the state itself.</param>
/// <param name="Provider">The provider it was begun for.</param>
/// <param name="Purpose"><see cref="ProviderSignInContext.Purpose"/>.</param>
/// <param name="Data"><see cref="ProviderSignInContext.Data"/>.</param>
/// <param name="ReturnTo"><see cref="ProviderSignInContext.ReturnTo"/>, already reduced to a local path.</param>
/// <param name="CodeVerifier">The PKCE verifier sent at the exchange.</param>
/// <param name="Nonce">The nonce Google's ID token must carry.</param>
/// <param name="CreatedAt">When it began.</param>
/// <param name="ExpiresAt">When it stops being good.</param>
public sealed record ProviderSignInState(
    string Key,
    string Provider,
    string Purpose,
    string? Data,
    string? ReturnTo,
    string CodeVerifier,
    string Nonce,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
