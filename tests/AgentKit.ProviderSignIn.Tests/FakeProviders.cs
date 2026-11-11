using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>An account at a stub provider: the id the provider vouches for, and the display hints it shares.</summary>
public sealed record StubAccount(string Id, string Name, string? Email);

/// <summary>
/// How the stub Google bends the ID token of one sign-in, to prove the package refuses it. Null fields keep the genuine
/// value.
/// </summary>
public sealed record IdTokenTweak(
    string? Audience = null,
    string? Issuer = null,
    string? Nonce = null,
    bool OmitNonce = false,
    bool Expired = false,
    bool ForgedSignature = false,
    bool OmitIdToken = false);

/// <summary>
/// A stub Google (authorize, token, JWKS) and Discord (authorize, token, users/@me) that behave like the real ones where it
/// matters: an authorization code is good once, for the client that asked for it, with its client secret, at the same
/// redirect URI, and only with the PKCE verifier whose S256 challenge the authorize request carried. Google's ID token is
/// RS256-signed with a key the JWKS publishes, issued by <c>https://accounts.google.com</c> for the client id, with the
/// authorize request's nonce. Every call reaches it through the package's named HttpClient; nothing touches the network.
/// </summary>
public sealed class FakeProviders : IDisposable
{
    public const string GoogleClientId = "stub-client.apps.googleusercontent.com";
    public const string GoogleClientSecret = "google-secret";
    public const string GoogleAuthorize = "https://accounts.google.test/o/oauth2/v2/auth";
    public const string GoogleToken = "https://oauth2.google.test/token";
    public const string GoogleJwks = "https://www.google.test/oauth2/v3/certs";
    public const string GoogleIssuer = "https://accounts.google.com";

    public const string DiscordClientId = "1100000000000000001";
    public const string DiscordClientSecret = "discord-secret";
    public const string DiscordAuthorize = "https://discord.test/oauth2/authorize";
    public const string DiscordToken = "https://discord.test/api/oauth2/token";
    public const string DiscordUser = "https://discord.test/api/users/@me";

    private const string KeyId = "stub-key-1";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _forger = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new();
    private readonly ConcurrentDictionary<string, StubAccount> _discordTokens = new();
    private readonly JsonWebTokenHandler _jwt = new();

    private sealed record Grant(string Provider, string ClientId, string RedirectUri, string Challenge, string? Nonce, StubAccount Account, IdTokenTweak Tweak);

    /// <summary>When set, the token endpoints answer with this status instead.</summary>
    public HttpStatusCode? TokenEndpointFailure { get; set; }

    /// <summary>The Discord user id the stub's users/@me reports instead of the account's (a malformed id, say).</summary>
    public string? DiscordIdOverride { get; set; }

    /// <summary>
    /// The browser at the provider: checks the authorize request as the provider would, "signs in" as
    /// <paramref name="account"/>, and returns where the provider sends the browser back (redirect URI, code, state).
    /// </summary>
    public Uri Approve(Uri authorizeUrl, StubAccount account, IdTokenTweak? tweak = null)
    {
        var provider = authorizeUrl.GetLeftPart(UriPartial.Path) switch
        {
            GoogleAuthorize => "google",
            DiscordAuthorize => "discord",
            var other => throw new InvalidOperationException($"No stub provider at {other}."),
        };
        var q = QueryHelpers.ParseQuery(authorizeUrl.Query);
        string Required(string name) => q.TryGetValue(name, out var v) && v.ToString() is { Length: > 0 } s
            ? s
            : throw new InvalidOperationException($"The {provider} authorize request has no {name}.");

        Assert.Equal("code", Required("response_type"));
        Assert.Equal(provider == "google" ? GoogleClientId : DiscordClientId, Required("client_id"));
        Assert.Equal("S256", Required("code_challenge_method"));
        var nonce = provider == "google" ? Required("nonce") : null;
        if (provider == "google") Assert.Contains("openid", Required("scope").Split(' '));
        var redirect = Required("redirect_uri");

        var code = "code-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Grant(provider, Required("client_id"), redirect, Required("code_challenge"), nonce, account, tweak ?? new IdTokenTweak());
        return new Uri(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = Required("state") }));
    }

    /// <summary>The provider sends the browser back with an error (the user pressed Cancel).</summary>
    public static Uri Deny(Uri authorizeUrl)
    {
        var q = QueryHelpers.ParseQuery(authorizeUrl.Query);
        return new Uri(QueryHelpers.AddQueryString(q["redirect_uri"].ToString(),
            new Dictionary<string, string?> { ["error"] = "access_denied", ["state"] = q["state"].ToString() }));
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        return (request.Method.Method, url) switch
        {
            ("GET", GoogleJwks) => Json(HttpStatusCode.OK, Jwks()),
            ("POST", GoogleToken) => await TokenAsync("google", request),
            ("POST", DiscordToken) => await TokenAsync("discord", request),
            ("GET", DiscordUser) => DiscordMe(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> TokenAsync(string provider, HttpRequestMessage request)
    {
        if (TokenEndpointFailure is { } failure) return new HttpResponseMessage(failure);
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
        string F(string name) => form.TryGetValue(name, out var v) ? v.ToString() : "";

        if (F("grant_type") != "authorization_code" || !_codes.TryRemove(F("code"), out var grant) || grant.Provider != provider)
            return Error("invalid_grant");
        var secret = provider == "google" ? GoogleClientSecret : DiscordClientSecret;
        if (F("client_id") != grant.ClientId || F("client_secret") != secret) return Error("invalid_client");
        if (F("redirect_uri") != grant.RedirectUri) return Error("invalid_grant");
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(F("code_verifier"))));
        if (F("code_verifier").Length < 43 || challenge != grant.Challenge) return Error("invalid_grant");

        var accessToken = "at-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        if (provider == "discord")
        {
            _discordTokens[accessToken] = grant.Account;
            return Json(HttpStatusCode.OK, new { access_token = accessToken, token_type = "Bearer", expires_in = 604800, scope = "identify" });
        }
        return grant.Tweak.OmitIdToken
            ? Json(HttpStatusCode.OK, new { access_token = accessToken, token_type = "Bearer", expires_in = 3599 })
            : Json(HttpStatusCode.OK, new { access_token = accessToken, token_type = "Bearer", expires_in = 3599, id_token = IdToken(grant) });
    }

    private HttpResponseMessage DiscordMe(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token } || !_discordTokens.TryGetValue(token, out var account))
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        return Json(HttpStatusCode.OK, new
        {
            id = DiscordIdOverride ?? account.Id,
            username = account.Name.ToLowerInvariant().Replace(' ', '_'),
            global_name = account.Name,
            email = account.Email,
            verified = account.Email is not null,
            avatar = (string?)null,
        });
    }

    private string IdToken(Grant grant)
    {
        var tweak = grant.Tweak;
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = grant.Account.Id,
            ["name"] = grant.Account.Name,
            ["azp"] = grant.ClientId,
        };
        if (grant.Account.Email is { } email)
        {
            claims["email"] = email;
            claims["email_verified"] = true;
        }
        if (!tweak.OmitNonce) claims["nonce"] = tweak.Nonce ?? grant.Nonce!;
        var key = new RsaSecurityKey(tweak.ForgedSignature ? _forger : _key) { KeyId = KeyId };
        return _jwt.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = tweak.Issuer ?? GoogleIssuer,
            Audience = tweak.Audience ?? grant.ClientId,
            IssuedAt = tweak.Expired ? now.AddHours(-2) : now,
            NotBefore = tweak.Expired ? now.AddHours(-2) : now,
            Expires = tweak.Expired ? now.AddHours(-1) : now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    private object Jwks()
    {
        var p = _key.ExportParameters(false);
        return new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64Url(p.Modulus!), e = Base64Url(p.Exponent!) } } };
    }

    private static HttpResponseMessage Error(string error) => Json(HttpStatusCode.BadRequest, new { error });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        _key.Dispose();
        _forger.Dispose();
    }
}

/// <summary>The transport the package's HttpClient gets: every request goes to the stubs. A fresh one per handler, the stubs shared.</summary>
public sealed class StubTransport(FakeProviders providers) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        providers.HandleAsync(request);
}
