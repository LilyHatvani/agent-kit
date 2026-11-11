using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentKit.ProviderSignIn.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace AgentKit.ProviderSignIn;

/// <summary>Names the package registers.</summary>
public static class ProviderSignInDefaults
{
    /// <summary>
    /// The named <see cref="HttpClient"/> every provider call goes through (token exchange, Discord's user, Google's keys).
    /// No redirects, no cookies, <see cref="ProviderSignInOptions.ProviderTimeout"/>. Tests swap its handler.
    /// </summary>
    public const string HttpClientName = "AgentKit.ProviderSignIn";
}

/// <summary>
/// The sign-in itself, the server as relying party. <see cref="BeginAsync"/> stores a single-use state (with a PKCE
/// verifier and a nonce), binds it to the browser and returns the provider's authorize URL; the mapped callback
/// (<see cref="ProviderSignInEndpointRouteBuilderExtensions.MapProviderSignIn"/>) consumes the state, exchanges the code
/// server-side, verifies the account and hands the host a <see cref="ProviderIdentity"/> through
/// <see cref="IProviderSignInCompletion"/>. No provider token is kept: the access token is used once, for Discord's
/// user read, and dropped with the response.
/// </summary>
public sealed class ProviderSignInFlow
{
    private readonly ProviderSignInOptions _options;
    private readonly IProviderSignInStateStore _store;
    private readonly IHttpClientFactory _http;
    private readonly GoogleIdTokens _google;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly StateCookie? _cookie;
    private readonly Dictionary<string, ProviderClientOptions> _enabled = new(StringComparer.Ordinal);

    internal ProviderSignInFlow(ProviderSignInOptions options, IProviderSignInStateStore store, IHttpClientFactory http, GoogleIdTokens google,
        TimeProvider clock, ILoggerFactory logs)
    {
        _options = options;
        _store = store;
        _http = http;
        _google = google;
        _clock = clock;
        _log = logs.CreateLogger("AgentKit.ProviderSignIn");
        if (options.PublicUri is { } publicUrl)
        {
            _cookie = new StateCookie(publicUrl);
            if (options.Google.IsConfigured) _enabled[SignInProviders.Google] = options.Google;
            if (options.Discord.IsConfigured) _enabled[SignInProviders.Discord] = options.Discord;
        }
        EnabledProviders = [.. _enabled.Keys];
    }

    /// <summary>The providers offered, in a stable order (Google, then Discord): those with a client id and secret. May be empty.</summary>
    public IReadOnlyList<string> EnabledProviders { get; }

    /// <summary>Whether <paramref name="provider"/> is offered (exact lowercase name).</summary>
    public bool IsEnabled(string provider) => provider is not null && _enabled.ContainsKey(provider);

    /// <summary>The redirect URI to register with the provider: <c>&lt;PublicUrl&gt;&lt;PathPrefix&gt;/{provider}/callback</c>.</summary>
    public Uri RedirectUri(string provider)
    {
        var publicUrl = _options.PublicUri ?? throw new InvalidOperationException("ProviderSignInOptions.PublicUrl is not set.");
        return new Uri(publicUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + _options.PathPrefix + "/" + provider + "/callback");
    }

    /// <summary>
    /// Begins a sign-in: stores its state, sets the browser binding cookie on <paramref name="context"/>'s response, and
    /// returns the provider's authorize URL to redirect to. The mapped <c>{provider}/start</c> calls this with
    /// <see cref="ProviderSignInContext.SignIn"/>; a host calls it from its own endpoints for other purposes (a setup that
    /// checked a setup token, a bind that checked the session) and gets that context back at the callback.
    /// </summary>
    /// <exception cref="ArgumentException">The provider is not enabled.</exception>
    public async Task<Uri> BeginAsync(string provider, HttpContext context, ProviderSignInContext? signIn = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_enabled.TryGetValue(provider ?? "", out var client) || _cookie is null)
            throw new ArgumentException($"The sign-in provider '{provider}' is not enabled.", nameof(provider));
        signIn ??= ProviderSignInContext.SignIn;
        ArgumentException.ThrowIfNullOrWhiteSpace(signIn.Purpose, nameof(signIn));

        var state = Random();
        var verifier = Random();
        var nonce = Random();
        var now = _clock.GetUtcNow();
        await _store.SaveAsync(new ProviderSignInState(Hash(state), provider!, signIn.Purpose, signIn.Data, LocalReturnTo(signIn.ReturnTo),
            verifier, nonce, now, now + _options.StateLifetime), now, cancellationToken);
        _cookie.Set(context, state, _options.StateLifetime);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = RedirectUri(provider!).AbsoluteUri,
            ["scope"] = client.Scope,
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
        };
        if (provider == SignInProviders.Google) query["nonce"] = nonce;
        if (provider == SignInProviders.Discord && _options.Discord.Prompt is { Length: > 0 } prompt) query["prompt"] = prompt;
        return new Uri(QueryHelpers.AddQueryString(client.AuthorizeEndpoint, query));
    }

    /// <summary>The callback: the browser binding, the state, the code exchange, the identity. Never throws for anything the browser or provider sent.</summary>
    internal async Task<(ProviderSignInSuccess? Success, ProviderSignInFailure? Failure)> CompleteAsync(string provider, HttpContext context, CancellationToken cancellationToken)
    {
        var client = _enabled[provider];
        // Read and delete the binding first, whatever happens next: a state is good for one callback.
        var bound = _cookie!.ReadAndDelete(context);
        var query = context.Request.Query;
        var state = query["state"].ToString();

        if (state.Length is 0 or > 200 || bound is null || !FixedTimeEquals(state, bound))
        {
            // An anonymous navigation: junk, a stale tab or another browser's state. Information, not a warning.
            _log.LogInformation("{Provider} sign-in refused: the state is missing or not this browser's.", provider);
            return Fail(provider, ProviderSignInError.StateInvalid, null);
        }

        var stored = await _store.TakeAsync(Hash(state), cancellationToken);
        if (stored is null || stored.Provider != provider)
        {
            _log.LogInformation("{Provider} sign-in refused: the state is unknown, already used, or another provider's.", provider);
            return Fail(provider, ProviderSignInError.StateInvalid, null);
        }
        var signIn = new ProviderSignInContext(stored.Purpose, stored.Data, stored.ReturnTo);
        if (stored.ExpiresAt <= _clock.GetUtcNow())
        {
            _log.LogInformation("{Provider} sign-in refused: the state expired.", provider);
            return Fail(provider, ProviderSignInError.StateExpired, signIn);
        }

        if (query["error"].ToString() is { Length: > 0 } error)
        {
            _log.LogInformation("{Provider} sign-in refused by the provider ({Error}).", provider, error.Length > 64 ? error[..64] : error);
            return Fail(provider, ProviderSignInError.Denied, signIn);
        }
        var code = query["code"].ToString();
        if (code.Length is 0 or > 2048)
        {
            _log.LogInformation("{Provider} sign-in refused: the callback carried no code.", provider);
            return Fail(provider, ProviderSignInError.ExchangeFailed, signIn);
        }

        try
        {
            using var http = _http.CreateClient(ProviderSignInDefaults.HttpClientName);
            var (error2, tokens) = await ExchangeAsync(http, provider, client, code, stored.CodeVerifier, cancellationToken);
            if (error2 is { } exchangeError) return Fail(provider, exchangeError, signIn);

            var (identityError, identity) = provider == SignInProviders.Google
                ? await GoogleIdentityAsync(tokens, stored.Nonce, cancellationToken)
                : await DiscordIdentityAsync(http, tokens, cancellationToken);
            return identity is null ? Fail(provider, identityError, signIn) : (new ProviderSignInSuccess(identity, signIn), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _log.LogWarning("{Provider} sign-in failed: the provider did not answer ({Reason}).", provider, e.GetType().Name);
            return Fail(provider, ProviderSignInError.ProviderUnavailable, signIn);
        }

        static (ProviderSignInSuccess?, ProviderSignInFailure?) Fail(string p, ProviderSignInError e, ProviderSignInContext? c) => (null, new ProviderSignInFailure(p, e, c));
    }

    private async Task<(ProviderSignInError? Error, JsonElement Tokens)> ExchangeAsync(HttpClient http, string provider, ProviderClientOptions client,
        string code, string verifier, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri(provider).AbsoluteUri,
            ["client_id"] = client.ClientId!,
            ["client_secret"] = client.ClientSecret!,
            ["code_verifier"] = verifier,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(client.TokenEndpoint)) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("{Provider} sign-in failed: the token endpoint answered {Status}.", provider, (int)response.StatusCode);
            return (InTrouble(response.StatusCode) ? ProviderSignInError.ProviderUnavailable : ProviderSignInError.ExchangeFailed, default);
        }
        var tokens = await ReadJsonAsync(response, cancellationToken);
        if (tokens.ValueKind != JsonValueKind.Object)
        {
            _log.LogWarning("{Provider} sign-in failed: the token endpoint's answer is not a JSON object.", provider);
            return (ProviderSignInError.ExchangeFailed, default);
        }
        return (null, tokens);
    }

    private async Task<(ProviderSignInError Error, ProviderIdentity? Identity)> GoogleIdentityAsync(JsonElement tokens, string nonce, CancellationToken cancellationToken)
    {
        var (status, token) = await _google.CheckAsync(String(tokens, "id_token"), nonce, cancellationToken);
        return status switch
        {
            IdTokenStatus.Valid => (default, new ProviderIdentity(SignInProviders.Google, token!.Subject, token.Name, token.Email, token.EmailVerified)),
            IdTokenStatus.Unavailable => (ProviderSignInError.ProviderUnavailable, null),
            _ => (ProviderSignInError.IdentityRejected, null),
        };
    }

    private async Task<(ProviderSignInError Error, ProviderIdentity? Identity)> DiscordIdentityAsync(HttpClient http, JsonElement tokens, CancellationToken cancellationToken)
    {
        if (String(tokens, "access_token") is not { Length: > 0 } accessToken)
        {
            _log.LogWarning("Discord sign-in failed: the token response carried no access token.");
            return (ProviderSignInError.ExchangeFailed, null);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.Discord.UserEndpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Discord sign-in failed: the user endpoint answered {Status}.", (int)response.StatusCode);
            return (InTrouble(response.StatusCode) ? ProviderSignInError.ProviderUnavailable : ProviderSignInError.IdentityRejected, null);
        }
        var user = await ReadJsonAsync(response, cancellationToken);
        // A Discord id is a snowflake: digits only, a 64-bit unsigned integer.
        if (user.ValueKind != JsonValueKind.Object || String(user, "id") is not { Length: > 0 and <= 20 } id || !id.All(char.IsAsciiDigit)
            || !ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            _log.LogWarning("Discord sign-in refused: the user has no usable id.");
            return (ProviderSignInError.IdentityRejected, null);
        }
        var name = String(user, "global_name") is { Length: > 0 } global ? global : String(user, "username");
        var verified = user.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True;
        return (default, new ProviderIdentity(SignInProviders.Discord, id, name, String(user, "email"), verified));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool InTrouble(HttpStatusCode status) => (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    /// <summary>
    /// A path on this site to return to, or null: a local path, or an absolute URL on the public origin, either one resolved
    /// (dot segments removed) and reduced to its path and query. A result that starts with <c>//</c> is refused: a host that
    /// redirects to it would send the browser to that host (<c>https://site//evil.example</c>, <c>/.//evil.example</c>).
    /// </summary>
    private string? LocalReturnTo(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\', StringComparison.Ordinal)) return null;
        var publicUrl = _options.PublicUri!;
        Uri? uri;
        if (value[0] == '/')
        {
            if (value.StartsWith("//", StringComparison.Ordinal) || !Uri.TryCreate(new Uri(publicUrl.GetLeftPart(UriPartial.Authority)), value, out uri)) return null;
        }
        else if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }
        if (Uri.Compare(uri, publicUrl, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0) return null;
        var local = uri.PathAndQuery;
        return local.StartsWith('/') && !local.StartsWith("//", StringComparison.Ordinal) ? local : null;
    }

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string state) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }
}
