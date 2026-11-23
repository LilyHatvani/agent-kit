using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentKit.Channels;

/// <summary>
/// Signed outgoing webhooks to the Standard Webhooks spec (standardwebhooks.com), which Zapier, Make, n8n and the
/// reference libraries verify: headers <c>webhook-id</c>, <c>webhook-timestamp</c> (Unix seconds) and
/// <c>webhook-signature</c> (<c>v1,</c> + base64 HMAC-SHA256 over <c>id.timestamp.body</c>, keyed by the secret's
/// bytes). The secret is <c>whsec_</c> + base64; the owner copies it once, into whatever receives the webhook.
/// </summary>
public static class StandardWebhooks
{
    /// <summary>How far a receiver lets a timestamp drift (the spec's reference value).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>A fresh secret: <c>whsec_</c> + base64 of 24 random bytes.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    /// <summary>Why <paramref name="url"/> can't take webhooks, or null: an absolute https URL without credentials.</summary>
    public static string? UrlRefusal(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0
            ? null
            : "A webhook address is an https URL without a user name or password.";
    }

    /// <summary>The <c>webhook-signature</c> value for one message.</summary>
    public static string Sign(string secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        var key = Key(secret);
        var signed = Encoding.UTF8.GetBytes($"{messageId}.{timestamp.ToString(CultureInfo.InvariantCulture)}.");
        using var hmac = new HMACSHA256(key);
        hmac.TransformBlock(signed, 0, signed.Length, null, 0);
        var bodyBytes = body.ToArray();
        hmac.TransformFinalBlock(bodyBytes, 0, bodyBytes.Length);
        return "v1," + Convert.ToBase64String(hmac.Hash!);
    }

    /// <summary>
    /// The POST for <paramref name="message"/>: <c>{"type", "timestamp", "data": {"title", "body", "url", …Data}}</c>,
    /// signed. <paramref name="messageId"/> stays the same across retries of one delivery, so a receiver can drop repeats.
    /// </summary>
    public static HttpRequestMessage Request(Uri url, string secret, string messageId, DateTimeOffset now, ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(message);
        var data = message.Data?.DeepClone().AsObject() ?? [];
        data["title"] = message.Title;
        data["body"] = message.Body;
        data["url"] = message.Link?.AbsoluteUri;
        var payload = new JsonObject
        {
            ["type"] = message.Type,
            ["timestamp"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["data"] = data,
        };
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var timestamp = now.ToUnixTimeSeconds();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("webhook-id", messageId);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", Sign(secret, messageId, timestamp, body));
        return request;
    }

    /// <summary>
    /// A receiver's check: the timestamp within <see cref="Tolerance"/> of <paramref name="now"/> and one of the
    /// space-separated <c>v1,</c> signatures matching, compared in constant time. For tests, and for a host that
    /// receives webhooks from another one.
    /// </summary>
    public static bool Verify(string secret, string messageId, string timestamp, string signatures, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (!IsFresh(timestamp, now)) return false;
        var ts = long.Parse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture);
        var expected = Encoding.ASCII.GetBytes(Sign(secret, messageId, ts, body));
        foreach (var candidate in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), expected)) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="timestamp"/> is Unix seconds (digits only) within <see cref="Tolerance"/> of
    /// <paramref name="now"/>, either side. Compared as seconds, so a timestamp past the year 9999 is stale, never a throw.
    /// </summary>
    public static bool IsFresh(string? timestamp, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return false;
        var nowSeconds = now.ToUnixTimeSeconds();
        var tolerance = (long)Tolerance.TotalSeconds;
        return ts >= nowSeconds - tolerance && ts <= nowSeconds + tolerance;
    }

    /// <summary>Why <paramref name="secret"/> can't sign or verify, or null: <c>whsec_</c> (optional) followed by non-empty base64.</summary>
    public static string? SecretRefusal(string? secret)
    {
        const string Why = "A webhook secret is whsec_ followed by base64.";
        if (string.IsNullOrEmpty(secret)) return Why;
        var b64 = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        var buffer = new byte[b64.Length];
        return Convert.TryFromBase64String(b64, buffer, out var n) && n > 0 ? null : Why;
    }

    private static byte[] Key(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var b64 = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        try { return Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException("A webhook secret is whsec_ followed by base64.", nameof(secret)); }
    }
}
