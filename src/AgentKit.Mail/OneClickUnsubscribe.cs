namespace AgentKit.Mail;

/// <summary>
/// The one-click unsubscribe pair of RFC 8058 (https://www.rfc-editor.org/rfc/rfc8058): <c>List-Unsubscribe:
/// &lt;https://…&gt;</c> and <c>List-Unsubscribe-Post: List-Unsubscribe=One-Click</c>. A mail client then POSTs
/// <c>List-Unsubscribe=One-Click</c> to the URL. That URL is the host's: it unsubscribes on the POST alone, with no
/// cookie, no sign-in and no redirect, so it carries its own token naming what to unsubscribe. Resend signs the mail
/// with DKIM over these headers, which RFC 8058 requires.
/// </summary>
public static class OneClickUnsubscribe
{
    /// <summary>The header naming the URL.</summary>
    public const string ListUnsubscribe = "List-Unsubscribe";

    /// <summary>The header saying the URL takes a one-click POST.</summary>
    public const string ListUnsubscribePost = "List-Unsubscribe-Post";

    /// <summary>The only value <see cref="ListUnsubscribePost"/> takes.</summary>
    public const string OneClick = "List-Unsubscribe=One-Click";

    /// <summary>Why <paramref name="url"/> can't be a one-click address, or null: an absolute https URL without credentials or a comma.</summary>
    public static string? UrlRefusal(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        // RFC 8058 §3.1: the URI MUST be https. A comma separates URIs in List-Unsubscribe (RFC 2369 §2), so it can't be in one.
        return url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0 && !url.AbsoluteUri.Contains(',', StringComparison.Ordinal)
            ? null
            : "A one-click unsubscribe address is an https URL without a user name, a password or a comma.";
    }

    /// <summary>
    /// The two headers for <paramref name="url"/>, in a new dictionary (names compared ignoring case) the host may add
    /// its other headers to before it sets <see cref="Email.Headers"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The URL isn't one (<see cref="UrlRefusal"/>).</exception>
    public static Dictionary<string, string> Headers(Uri url)
    {
        if (UrlRefusal(url) is { } why) throw new ArgumentException(why, nameof(url));
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ListUnsubscribe] = $"<{url.AbsoluteUri}>",
            [ListUnsubscribePost] = OneClick,
        };
    }
}
