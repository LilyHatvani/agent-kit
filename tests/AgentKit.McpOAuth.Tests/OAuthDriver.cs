using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace AgentKit.McpOAuth.Tests;

/// <summary>Drives the OAuth flow the way a hosted connector and its owner's browser do.</summary>
public static partial class OAuthDriver
{
    public const string ClaudeAiCallback = "https://claude.ai/api/mcp/auth_callback";

    public sealed record Pkce(string Verifier, string Challenge)
    {
        public static Pkce New()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            return new Pkce(verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        }
    }

    public sealed record Approved(string Code, Uri Location, string? Iss);

    public sealed record Connected(Guid ConnectionId, string AccessToken, string RefreshToken, JsonObject Tokens, Uri Location, string? Iss);

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewLabel() => "test-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public static string AuthorizeUrl(string clientId, string redirect, string challenge, string state, string? resource = null, string method = "S256") =>
        QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["redirect_uri"] = redirect, ["scope"] = OAuthApp.Scope,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = method, ["resource"] = resource ?? OAuthApp.Resource,
        });

    /// <summary>The whole flow: authorize, the owner's two consent steps, the code exchange. The connection is looked up by its label.</summary>
    public static async Task<Connected> ConnectAsync(OAuthApp app, McpHost host, string clientId, string redirect,
        Dictionary<string, string>? choices = null)
    {
        using var http = host.Browser();
        var pkce = Pkce.New();
        choices ??= [];
        choices.TryAdd("label", NewLabel());
        var approved = await ApproveAsync(app, http, clientId, redirect, pkce, choices);
        var r = await http.PostAsync("/oauth/token", Form(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = approved.Code, ["redirect_uri"] = redirect,
            ["client_id"] = clientId, ["code_verifier"] = pkce.Verifier, ["resource"] = OAuthApp.Resource,
        }));
        var tokens = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
        Assert.True(r.IsSuccessStatusCode, tokens.ToJsonString());
        var connection = app.Book.FindByLabel(choices["label"]) ?? throw new InvalidOperationException("No connection was created.");
        return new Connected(connection.Id, tokens["access_token"]!.GetValue<string>(), tokens["refresh_token"]!.GetValue<string>(),
            tokens, approved.Location, approved.Iss);
    }

    /// <summary>The browser part: the page, the owner secret, the choices (page defaults overridden by <paramref name="choices"/>), Allow.</summary>
    public static async Task<Approved> ApproveAsync(OAuthApp app, HttpClient http, string clientId, string redirect, Pkce pkce,
        Dictionary<string, string> choices)
    {
        var state = "st-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var page = await http.GetAsync(AuthorizeUrl(clientId, redirect, pkce.Challenge, state));
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"{page.StatusCode} {page.Headers.Location} {html}");

        var unlocked = await Post(http, Hidden(html), new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" });
        var unlockedHtml = await unlocked.Content.ReadAsStringAsync();
        Assert.True(unlocked.StatusCode == HttpStatusCode.OK, unlockedHtml);
        return await AllowAsync(http, unlockedHtml, redirect, state, choices);
    }

    /// <summary>Allow on the choices page, with the page's own defaults unless <paramref name="choices"/> overrides them.</summary>
    public static async Task<Approved> AllowAsync(HttpClient http, string choicesHtml, string redirect, string state, Dictionary<string, string> choices)
    {
        var form = Hidden(choicesHtml);
        if (!choices.ContainsKey("agent")) form.Add(new("agent", SelectedAgent(choicesHtml)));
        var allowed = await Post(http, form, new(choices) { ["consent"] = "allow" });
        Assert.True(allowed.StatusCode == HttpStatusCode.Redirect, $"{allowed.StatusCode} {await allowed.Content.ReadAsStringAsync()}");
        var location = allowed.Headers.Location!;
        Assert.StartsWith(redirect, location.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(state, query["state"].ToString());
        return new Approved(query["code"].ToString(), location, query.TryGetValue("iss", out var iss) ? iss.ToString() : null);
    }

    public static string SelectedAgent(string html) => SelectedOption().Match(html).Groups[1].Value;

    public static async Task<HttpResponseMessage> Post(HttpClient http, List<KeyValuePair<string, string>> hidden, Dictionary<string, string> extra)
    {
        var fields = hidden.Where(kv => !extra.ContainsKey(kv.Key)).Concat(extra).ToList();
        return await http.PostAsync("/oauth/authorize", new FormUrlEncodedContent(fields));
    }

    public static List<KeyValuePair<string, string>> Hidden(string html) =>
        HiddenInput().Matches(html).Select(m => new KeyValuePair<string, string>(
            WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();

    public static async Task<(HttpStatusCode Status, JsonObject Json)> RefreshAsync(HttpClient http, string refreshToken, string? resource = null)
    {
        var r = await http.PostAsync("/oauth/token", Form(new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = "claude-ai", ["resource"] = resource ?? OAuthApp.Resource,
        }));
        return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
    }

    public static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    public static async Task<JsonObject> Json(HttpResponseMessage r)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode} {body}");
        return JsonNode.Parse(body)!.AsObject();
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenInput();

    [GeneratedRegex("<option value=\"([^\"]+)\" selected>")]
    private static partial Regex SelectedOption();
}
