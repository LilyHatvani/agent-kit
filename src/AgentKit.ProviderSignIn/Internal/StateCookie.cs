using Microsoft.AspNetCore.Http;

namespace AgentKit.ProviderSignIn.Internal;

/// <summary>
/// Binds a sign-in to the browser that began it: the state the provider echoes back must equal this cookie. Without it,
/// anyone could send the owner's browser to the callback with the attacker's own code and state (login CSRF), and during
/// setup or a bind that would attach the attacker's account. On https the name carries the <c>__Host-</c> prefix, so the
/// browser refuses a copy planted by a sibling subdomain (one with a Domain, another path, or no Secure); it is read by
/// its exact-case name only, and a request carrying two copies is refused. SameSite=Lax: the provider's redirect back is
/// a top-level GET, which carries it.
/// </summary>
internal sealed class StateCookie
{
    private readonly bool _secure;

    public StateCookie(Uri publicUrl)
    {
        _secure = publicUrl.Scheme == Uri.UriSchemeHttps;
        Name = _secure ? "__Host-agentkit_signin" : "agentkit_signin";
    }

    public string Name { get; }

    public void Set(HttpContext context, string state, TimeSpan lifetime) =>
        context.Response.Cookies.Append(Name, state, Options(lifetime));

    /// <summary>Reads the cookie (exact name, exactly one copy) and always deletes it: a state is good for one callback.</summary>
    public string? ReadAndDelete(HttpContext context)
    {
        string? found = null;
        var copies = 0;
        foreach (var header in context.Request.Headers.Cookie)
        {
            if (header is null) continue;
            foreach (var part in header.Split(';'))
            {
                var pair = part.Trim();
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || !string.Equals(pair[..eq], Name, StringComparison.Ordinal)) continue;
                found = pair[(eq + 1)..];
                copies++;
            }
        }
        context.Response.Cookies.Delete(Name, Options(null));
        return copies == 1 && found is { Length: > 0 } ? found : null;
    }

    private CookieOptions Options(TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = _secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime,
        IsEssential = true,
    };
}
