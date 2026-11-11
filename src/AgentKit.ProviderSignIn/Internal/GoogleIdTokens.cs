using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgentKit.ProviderSignIn.Internal;

/// <summary>The outcome of checking a Google ID token.</summary>
internal enum IdTokenStatus
{
    Invalid = 0,
    Unavailable,
    Valid,
}

/// <summary>A checked Google ID token: the claims the sign-in uses.</summary>
internal sealed record GoogleIdToken(string Subject, string? Name, string? Email, bool EmailVerified);

/// <summary>
/// Checks the ID token Google returns from the code exchange: RS256 signature against Google's published keys, issuer
/// (either documented spelling), audience (this client id), expiry, a non-empty <c>sub</c>, and the nonce this sign-in
/// stored. The keys are cached for an hour and fetched again once when a token names a key the cache does not know
/// (Google rotates them), at most once a minute, so forged key ids cannot make this server hammer Google.
/// </summary>
internal sealed class GoogleIdTokens(ProviderSignInOptions options, IHttpClientFactory http, TimeProvider clock, ILoggerFactory logs) : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefresh = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly ILogger _log = logs.CreateLogger("AgentKit.ProviderSignIn");
    private IList<SecurityKey>? _keys;
    private DateTimeOffset _fetchedAt;

    public async Task<(IdTokenStatus Status, GoogleIdToken? Token)> CheckAsync(string? idToken, string expectedNonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            _log.LogWarning("Google sign-in refused: the token response carried no ID token.");
            return (IdTokenStatus.Invalid, null);
        }

        var keys = await KeysAsync(refresh: false, cancellationToken);
        if (keys is null) return (IdTokenStatus.Unavailable, null);
        var result = await ValidateAsync(idToken, keys);
        if (result is { IsValid: false, Exception: SecurityTokenSignatureKeyNotFoundException })
        {
            keys = await KeysAsync(refresh: true, cancellationToken);
            if (keys is null) return (IdTokenStatus.Unavailable, null);
            result = await ValidateAsync(idToken, keys);
        }

        if (!result.IsValid)
        {
            // The exception's type only: token library messages can echo token content.
            _log.LogWarning("Google sign-in refused: the ID token failed validation ({Reason}).", result.Exception?.GetType().Name ?? "unknown");
            return (IdTokenStatus.Invalid, null);
        }

        var subject = Claim(result, "sub");
        if (string.IsNullOrEmpty(subject))
        {
            _log.LogWarning("Google sign-in refused: the ID token has no subject.");
            return (IdTokenStatus.Invalid, null);
        }
        if (!FixedTimeEquals(Claim(result, "nonce"), expectedNonce))
        {
            // Another sign-in's ID token, or one with no nonce: a code injected into this callback.
            _log.LogWarning("Google sign-in refused: the ID token's nonce is not this sign-in's.");
            return (IdTokenStatus.Invalid, null);
        }

        var verified = result.Claims.TryGetValue("email_verified", out var v) && v switch
        {
            bool b => b,
            string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        return (IdTokenStatus.Valid, new GoogleIdToken(subject, Claim(result, "name"), Claim(result, "email"), verified));
    }

    private Task<TokenValidationResult> ValidateAsync(string idToken, IList<SecurityKey> keys) =>
        _handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = options.Google.EffectiveIssuers,
            ValidateIssuer = true,
            ValidAudience = options.Google.ClientId,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(2),
        });

    private async Task<IList<SecurityKey>?> KeysAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (_keys is not null && now - _fetchedAt < (refresh ? MinRefresh : MaxAge)) return _keys;
            try
            {
                using var client = http.CreateClient(ProviderSignInDefaults.HttpClientName);
                using var response = await client.GetAsync(new Uri(options.Google.JwksUri), cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Google's signing keys answered {Status}.", (int)response.StatusCode);
                    return _keys;
                }
                var set = new JsonWebKeySet(await response.Content.ReadAsStringAsync(cancellationToken));
                _keys = set.GetSigningKeys();
                _fetchedAt = now;
                return _keys;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException && !cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning("Google's signing keys could not be fetched ({Reason}); Google sign-in fails closed.", e.GetType().Name);
                return _keys;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string? Claim(TokenValidationResult result, string name) =>
        result.Claims.TryGetValue(name, out var value) ? value as string : null;

    private static bool FixedTimeEquals(string? actual, string expected)
    {
        if (actual is null) return false;
        var a = Encoding.UTF8.GetBytes(actual);
        var e = Encoding.UTF8.GetBytes(expected);
        return a.Length == e.Length && CryptographicOperations.FixedTimeEquals(a, e);
    }
}
