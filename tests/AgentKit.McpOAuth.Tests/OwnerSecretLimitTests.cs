using System.Net;
using static AgentKit.McpOAuth.Tests.OAuthDriver;

namespace AgentKit.McpOAuth.Tests;

/// <summary>Relay's security review 2026-11-11: the shared owner secret could be guessed at without limit.</summary>
public sealed class OwnerSecretLimitTests(OAuthApp app)
{
    private static async Task<HttpResponseMessage> TryAsync(HttpClient http, string secret)
    {
        var page = await http.GetAsync(AuthorizeUrl("claude-ai", ClaudeAiCallback, Pkce.New().Challenge, "guess"));
        return await Post(http, Hidden(await page.Content.ReadAsStringAsync()), new() { ["password"] = secret, ["consent"] = "continue" });
    }

    [Fact]
    public async Task AfterTooManyWrongSecrets_EvenTheRightOneIsRefused_UntilTheWindowEnds()
    {
        var clock = new ManualClock();
        var check = new McpSecretOwnerCheck(() => app.OwnerSecret) { MaxFailedAttempts = 3, FailedAttemptWindow = TimeSpan.FromMinutes(15) };
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl, check, clock: clock);
        using var http = host.Browser();

        for (var i = 0; i < 3; i++)
        {
            var wrong = await TryAsync(http, "guess-" + i);
            Assert.DoesNotContain("name=\"unlock\"", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var locked = await TryAsync(http, app.OwnerSecret);
        var lockedHtml = await locked.Content.ReadAsStringAsync();
        Assert.DoesNotContain("name=\"unlock\"", lockedHtml, StringComparison.Ordinal);
        Assert.Contains("Too many attempts", lockedHtml, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(16));
        var open = await TryAsync(http, app.OwnerSecret);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Contains("name=\"unlock\"", await open.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FewerWrongSecretsThanTheLimit_LeaveTheRightOneWorking()
    {
        var check = new McpSecretOwnerCheck(() => app.OwnerSecret) { MaxFailedAttempts = 3 };
        await using var host = await app.StartHostAsync(OAuthApp.PublicUrl, check);
        using var http = host.Browser();

        await TryAsync(http, "guess-a");
        await TryAsync(http, "guess-b");
        var open = await TryAsync(http, app.OwnerSecret);
        Assert.Contains("name=\"unlock\"", await open.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
