using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentKit.Social.Internal;

/// <summary>
/// OAuth 1.0a request signing, HMAC-SHA1 (RFC 5849 §3.4), for X's user-context tokens. Lifted from Echo of Dreams'
/// announcement cross-poster, where it signs every X call.
/// <para>
/// Signed: the oauth_* values, the URL's query and a form-urlencoded body. Not signed: a JSON or multipart body
/// (RFC 5849 §3.4.1.3.1), which is every X v2 call this package makes. Pinned by X's published worked example
/// (https://docs.x.com/resources/fundamentals/authentication/oauth-1-0a/creating-a-signature).
/// </para>
/// </summary>
internal static class OAuth1Signer
{
    public sealed record Credentials(string ConsumerKey, string ConsumerSecret, string Token, string TokenSecret)
    {
        public override string ToString() => "OAuth 1.0a credentials (hidden)";
    }

    /// <summary>The <c>Authorization</c> value (<c>OAuth oauth_consumer_key="…", …</c>) for one request.</summary>
    public static string AuthorizationHeader(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> formParameters,
        Credentials credentials, string nonce, long timestamp)
    {
        var oauth = OAuthParameters(credentials, nonce, timestamp);
        var signature = Signature(method, url, oauth.Concat(formParameters), credentials);
        var header = oauth
            .Append(new("oauth_signature", signature))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{Encode(p.Key)}=\"{Encode(p.Value)}\"");
        return "OAuth " + string.Join(", ", header);
    }

    /// <summary>The base64 HMAC-SHA1 signature over the signature base string; the URL's own query is read off <paramref name="url"/>.</summary>
    public static string Signature(HttpMethod method, Uri url, IEnumerable<KeyValuePair<string, string>> parameters, Credentials credentials)
    {
        var all = parameters.Concat(QueryParameters(url))
            .Select(p => (Key: Encode(p.Key), Value: Encode(p.Value)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}");
        var parameterString = string.Join("&", all);
        var baseString = $"{method.Method.ToUpperInvariant()}&{Encode(BaseUri(url))}&{Encode(parameterString)}";
        var key = $"{Encode(credentials.ConsumerSecret)}&{Encode(credentials.TokenSecret)}";
#pragma warning disable CA5350 // HMAC-SHA1 is what OAuth 1.0a (and X) specify; the key is the shared secret, not a password hash.
        return Convert.ToBase64String(HMACSHA1.HashData(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(baseString)));
#pragma warning restore CA5350
    }

    /// <summary>32 random bytes as letters and digits: unique per request.</summary>
    public static string NewNonce() =>
        new([.. Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Where(char.IsAsciiLetterOrDigit)]);

    /// <summary>RFC 3986 percent-encoding: everything but ALPHA / DIGIT / "-" / "." / "_" / "~".</summary>
    internal static string Encode(string value) => Uri.EscapeDataString(value);

    private static List<KeyValuePair<string, string>> OAuthParameters(Credentials c, string nonce, long timestamp) =>
    [
        new("oauth_consumer_key", c.ConsumerKey),
        new("oauth_nonce", nonce),
        new("oauth_signature_method", "HMAC-SHA1"),
        new("oauth_timestamp", timestamp.ToString(CultureInfo.InvariantCulture)),
        new("oauth_token", c.Token),
        new("oauth_version", "1.0"),
    ];

    /// <summary>Scheme and host lower-cased, a default port dropped, no query or fragment (RFC 5849 §3.4.1.2).</summary>
    private static string BaseUri(Uri url)
    {
        var port = url.IsDefaultPort ? "" : string.Create(CultureInfo.InvariantCulture, $":{url.Port}");
        // Uri already lower-cases the scheme and a DNS host.
        return $"{url.Scheme}://{url.Host}{port}{url.AbsolutePath}";
    }

    private static IEnumerable<KeyValuePair<string, string>> QueryParameters(Uri url)
    {
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) yield break;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=', StringComparison.Ordinal);
            var name = i < 0 ? pair : pair[..i];
            var value = i < 0 ? "" : pair[(i + 1)..];
            yield return new(Uri.UnescapeDataString(name.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' ')));
        }
    }
}
