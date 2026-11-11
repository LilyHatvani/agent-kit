using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AgentKit.McpOAuth.Internal;

/// <summary>The consent page's HTML: no scripts, nothing external, never framed.</summary>
internal sealed class ConsentPage(McpOAuthOptions options, McpConsentRequest request, IReadOnlyList<KeyValuePair<string, string>> oauthHidden)
{
    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string CredentialStep(string fieldsHtml) =>
        $"""
        {fieldsHtml}
        <div class="row">
          <button class="allow" type="submit" name="consent" value="continue">Continue</button>
          <button type="submit" name="consent" value="deny" formnovalidate>Deny</button>
        </div>
        """;

    public static string ChoicesStep(string choicesHtml, string unlock) =>
        $"""
        <input type="hidden" name="unlock" value="{H(unlock)}">
        {choicesHtml}
        <div class="row">
          <button class="allow" type="submit" name="consent" value="allow">Allow</button>
          <button type="submit" name="consent" value="deny" formnovalidate>Deny</button>
        </div>
        """;

    public IResult Render(HttpContext ctx, string body, string? error, int status)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // No form-action: after the POST the browser follows a redirect to the client, which form-action would block.
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";
        ctx.Response.Headers.XFrameOptions = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Content(Html(body, error), "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private string Html(string body, string? error)
    {
        var redirect = request.RedirectUri;
        var returnsTo = request.RedirectsToLoopback
            ? $"<p class=\"warn\">It signs in through <strong>a program on this computer</strong> ({H(redirect.Host)}, port {redirect.Port}). Allow only if you just started this from an app you run here.</p>"
            : $"<p>After you choose, you go back to <strong>{H(redirect.Host)}</strong>.</p>";
        var from = request.MetadataDocument is { } doc ? $" <span class=\"muted\">(from {H(doc.Host)})</span>" : "";
        var hidden = string.Concat(oauthHidden.Select(kv => $"<input type=\"hidden\" name=\"{H(kv.Key)}\" value=\"{H(kv.Value)}\">"));
        var description = options.ConsentDescription is { } d ? $"<p>{H(d)}</p>" : "";
        var errorHtml = error is null ? "" : $"<p class=\"error\" role=\"alert\">{H(error)}</p>";

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Connect to {{H(options.ResourceName)}}</title>
            <style>
              :root{color-scheme:light dark}
              body{font-family:system-ui,sans-serif;max-width:32rem;margin:2rem auto;padding:0 1rem;line-height:1.5}
              h1{font-size:1.3rem} label{display:block;margin-top:1rem;font-weight:600}
              input:not([type=checkbox]):not([type=radio]),select{box-sizing:border-box;width:100%;font:inherit;padding:.6rem;margin-top:.3rem;border-radius:.4rem;border:1px solid #999}
              fieldset{border:1px solid #9995;border-radius:.4rem;margin-top:1rem} legend{font-weight:600}
              label.check{font-weight:400;margin-top:.4rem} .indent{margin-left:1.5rem}
              .row{display:flex;gap:.75rem;margin-top:1.5rem} button{font:inherit;padding:.7rem 1.2rem;border-radius:.5rem;border:1px solid #999;min-height:44px}
              .allow{background:#2f6fde;color:#fff;border-color:#2f6fde} .muted{opacity:.7;font-size:.9rem;font-weight:400}
              .warn{border:1px solid #d9a400;border-radius:.4rem;padding:.6rem .8rem} .error{border:1px solid #c33;border-radius:.4rem;padding:.6rem .8rem;color:#c33}
            </style></head><body>
            <h1>Connect <strong>{{H(request.ClientName)}}</strong>{{from}} to {{H(options.ResourceName)}}</h1>
            {{description}}
            {{returnsTo}}
            {{errorHtml}}
            <form method="post" action="authorize">
            {{hidden}}
            {{body}}
            </form>
            </body></html>
            """;
    }
}
