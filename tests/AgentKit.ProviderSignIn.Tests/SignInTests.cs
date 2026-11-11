using System.Net;
using Microsoft.AspNetCore.WebUtilities;

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>A sign-in that should succeed hands the host the provider's stable account id, and the context the host began it with.</summary>
public sealed class SignInTests(SignInDatabase database)
{
    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task Google_SignIn_HandsTheHostTheGoogleSubjectAsTheAccountId(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var account = Accounts.Google();

        var (status, body) = await Browser.SignInAsync(host, providers, "google", account);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("google", body["provider"]!.GetValue<string>());
        Assert.Equal(account.Id, body["accountId"]!.GetValue<string>());
        Assert.Equal(account.Name, body["name"]!.GetValue<string>());
        Assert.Equal(account.Email, body["email"]!.GetValue<string>());
        Assert.True(body["emailVerified"]!.GetValue<bool>());
        Assert.Equal(ProviderSignInPurposes.SignIn, body["purpose"]!.GetValue<string>());
        Assert.Single(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task Discord_SignIn_HandsTheHostTheDiscordUserId(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var account = Accounts.Discord();

        var (status, body) = await Browser.SignInAsync(host, providers, "discord", account);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("discord", body["provider"]!.GetValue<string>());
        Assert.Equal(account.Id, body["accountId"]!.GetValue<string>());
        Assert.Equal(account.Name, body["name"]!.GetValue<string>());
        Assert.Null(body["email"]);
        Assert.Single(host.SignedIn);
    }

    [Theory, InlineData("google"), InlineData("discord")]
    public async Task Start_SendsTheBrowserToTheProviderWithPkceS256AndTheOneRegisteredRedirect(string provider)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var started = await Browser.StartAsync(host, provider);

        var q = QueryHelpers.ParseQuery(started.Authorize.Query);
        Assert.Equal($"{SignInHost.PublicUrl}/api/auth/{provider}/callback", q["redirect_uri"].ToString());
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Equal(43, q["code_challenge"].ToString().Length);
        Assert.True(q["state"].ToString().Length >= 43);
        if (provider == "google") Assert.True(q["nonce"].ToString().Length >= 43);
        // The browser binding: a host-only, https-only cookie scripts cannot read, sent on the provider's top-level redirect back.
        Assert.StartsWith("__Host-", started.Cookie, StringComparison.Ordinal);
        Assert.Contains("secure", started.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", started.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", started.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", started.SetCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", started.SetCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory, InlineData("google"), InlineData("discord")]
    public async Task Start_TwiceInARow_GivesEachSignInItsOwnStateAndVerifier(string provider)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var first = QueryHelpers.ParseQuery((await Browser.StartAsync(host, provider)).Authorize.Query);
        var second = QueryHelpers.ParseQuery((await Browser.StartAsync(host, provider)).Authorize.Query);

        Assert.NotEqual(first["state"].ToString(), second["state"].ToString());
        Assert.NotEqual(first["code_challenge"].ToString(), second["code_challenge"].ToString());
    }

    [Fact]
    public async Task SignIn_StartedOnOneReplica_CompletesOnAnotherSharingPostgres()
    {
        using var providers = new FakeProviders();
        await using var a = await SignInHost.StartAsync(providers, database, Store.Postgres);
        await using var b = await SignInHost.StartAsync(providers, database, Store.Postgres);
        var account = Accounts.Google();

        var started = await Browser.StartAsync(a, "google");
        var (status, body) = await Browser.CallbackAsync(b, providers.Approve(started.Authorize, account), started.Cookie);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(account.Id, body["accountId"]!.GetValue<string>());
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task HostBegunSignIn_ComesBackWithTheHostsPurposeDataAndReturnTo(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var account = Accounts.Discord();

        var started = await Browser.FollowStartAsync(host, HttpMethod.Post, "/setup?provider=discord");
        var (status, body) = await Browser.CallbackAsync(host, providers.Approve(started.Authorize, account), started.Cookie);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(account.Id, body["accountId"]!.GetValue<string>());
        Assert.Equal("setup", body["purpose"]!.GetValue<string>());
        Assert.Equal("setup-data", body["data"]!.GetValue<string>());
        Assert.Equal("/welcome", body["returnTo"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/settings?tab=identities", "/settings?tab=identities")]
    [InlineData(SignInHost.PublicUrl + "/oauth/authorize?client_id=claude-ai&state=x", "/oauth/authorize?client_id=claude-ai&state=x")]
    [InlineData("https://evil.example/phish", null)]
    [InlineData("//evil.example/phish", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("https://app.test.evil.example/", null)]
    [InlineData("http://app.test/downgraded", null)]
    [InlineData(SignInHost.PublicUrl + "//evil.example/phish", null)]
    [InlineData(SignInHost.PublicUrl + "/.//evil.example/phish", null)]
    [InlineData("/.//evil.example/phish", null)]
    [InlineData("/a/..//evil.example/phish", null)]
    public async Task ReturnTo_IsKeptOnlyAsAPathOnThisSite(string returnTo, string? expected)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var started = await Browser.StartAsync(host, "google", returnTo);
        var (status, body) = await Browser.CallbackAsync(host, providers.Approve(started.Authorize, Accounts.Google()), started.Cookie);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body["returnTo"]?.GetValue<string>());
    }
}
