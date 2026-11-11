using System.Net;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>
/// Relay's security review 2026-11-11: the consent unlock is single-use (across replicas), bound to the browser that
/// proved the owner, and covers the requested scope.
/// </summary>
public sealed class ConsentUnlockTests(OAuthApp app)
{
    /// <summary>The page after the owner secret: the choices, with the unlock among the hidden fields.</summary>
    private async Task<(string Html, List<KeyValuePair<string, string>> Form)> UnlockedAsync(HttpClient http, string state, string? scope = null)
    {
        var url = AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, state);
        if (scope is not null) url = url.Replace("scope=" + OAuthApp.Scope, "scope=" + Uri.EscapeDataString(scope), StringComparison.Ordinal);
        var page = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var unlocked = await Post(http, Hidden(await page.Content.ReadAsStringAsync()), new() { ["password"] = app.OwnerSecret, ["consent"] = "continue" });
        var html = await unlocked.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Contains("name=\"unlock\"", html, StringComparison.Ordinal);
        return (html, Hidden(html));
    }

    private static Dictionary<string, string> Allow(string label) => new() { ["consent"] = "allow", ["agent"] = "claude", ["label"] = label };

    [Fact]
    public async Task AnUnlock_GrantsOnce_ASecondAllowWithIt_OnThisHostOrAReplica_GrantsNothing()
    {
        using var http = app.Host.Browser();
        var (_, form) = await UnlockedAsync(http, "once");

        var first = await Post(http, form, Allow(NewLabel()));
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var after = app.Book.All.Count;

        var again = await Post(http, form, Allow(NewLabel()));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Null(again.Headers.Location);
        Assert.Contains("name=\"unlock\"", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal); // a fresh one to confirm anew

        // A replica on the same database knows it was used: the browser's cookie and the unlock are both valid there.
        await using var replica = await app.StartHostAsync(OAuthApp.PublicUrl);
        var cookie = await CookiesOf(http);
        using var sameBrowserOnReplica = replica.Browser(cookie);
        var onReplica = await Post(sameBrowserOnReplica, form, Allow(NewLabel()));
        Assert.Equal(HttpStatusCode.BadRequest, onReplica.StatusCode);
        Assert.Null(onReplica.Headers.Location);

        Assert.Equal(after, app.Book.All.Count);
    }

    [Fact]
    public async Task TwoAllowsAtOnce_WithOneUnlock_MakeOneConnection()
    {
        using var http = app.Host.Browser();
        var (_, form) = await UnlockedAsync(http, "double");
        var before = app.Book.All.Count;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Post(http, form, Allow(NewLabel()))));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Redirect));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Redirect), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(before + 1, app.Book.All.Count);
    }

    [Fact]
    public async Task AnUnlock_WorksOnlyInTheBrowserThatProvedTheOwner()
    {
        using var owner = app.Host.Browser();
        var (html, form) = await UnlockedAsync(owner, "bound");

        // Another browser with the copied form: no cookie at all, or a cookie of its own from its own consent page.
        using var stranger = app.Host.Browser();
        var bare = await Post(stranger, form, Allow(NewLabel()));
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Null(bare.Headers.Location);
        await UnlockedAsync(stranger, "stranger-own");
        var withOwnCookie = await Post(stranger, form, Allow(NewLabel()));
        Assert.Equal(HttpStatusCode.BadRequest, withOwnCookie.StatusCode);
        Assert.Null(withOwnCookie.Headers.Location);

        // The owner's browser still can: the strangers' attempts did not use the unlock up.
        await AllowAsync(owner, html, ClaudeAiCallback, "bound", new() { ["label"] = NewLabel() });
    }

    [Fact]
    public async Task AnUnlock_IsBoundToTheRequestedScope()
    {
        using var http = app.Host.Browser();
        var (_, form) = await UnlockedAsync(http, "scope", scope: OAuthApp.Scope);
        var before = app.Book.All.Count;

        var widened = form.Where(kv => kv.Key != "scope").Append(new("scope", OAuthApp.Scope + " offline_access")).ToList();
        var r = await Post(http, widened, Allow(NewLabel()));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
        Assert.Equal(before, app.Book.All.Count);
    }
}
