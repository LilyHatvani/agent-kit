using System.Net;

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>
/// The state is good once, for ten minutes, in the browser that began the sign-in, for the provider it was begun with.
/// Every refusal leaves the host without an identity.
/// </summary>
public sealed class StateTests(SignInDatabase database)
{
    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task ReplayedCallback_IsRefusedAfterTheFirstSucceeds(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var started = await Browser.StartAsync(host, "google");
        var callback = providers.Approve(started.Authorize, Accounts.Google());

        var (first, _) = await Browser.CallbackAsync(host, callback, started.Cookie);
        var (second, body) = await Browser.CallbackAsync(host, callback, started.Cookie);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.BadRequest, second);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Single(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task ReplayedState_WithAFreshCodeForIt_IsStillRefused(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var started = await Browser.StartAsync(host, "discord");
        await Browser.CallbackAsync(host, providers.Approve(started.Authorize, Accounts.Discord()), started.Cookie);

        // The provider would happily mint another code for the same authorize request; the state is spent all the same.
        var (status, body) = await Browser.CallbackAsync(host, providers.Approve(started.Authorize, Accounts.Discord()), started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Single(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task StateOlderThanTenMinutes_IsRefusedAsExpired(Store store)
    {
        using var providers = new FakeProviders();
        var clock = new TestClock();
        await using var host = await SignInHost.StartAsync(providers, database, store, clock);
        var started = await Browser.StartAsync(host, "google");
        var callback = providers.Approve(started.Authorize, Accounts.Google());

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var (status, body) = await Browser.CallbackAsync(host, callback, started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("state_expired", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task StateYoungerThanTenMinutes_StillCompletes(Store store)
    {
        using var providers = new FakeProviders();
        var clock = new TestClock();
        await using var host = await SignInHost.StartAsync(providers, database, store, clock);
        var started = await Browser.StartAsync(host, "google");
        var callback = providers.Approve(started.Authorize, Accounts.Google());

        clock.Advance(TimeSpan.FromMinutes(9));
        var (status, _) = await Browser.CallbackAsync(host, callback, started.Cookie);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task Callback_InABrowserThatDidNotStartTheSignIn_IsRefusedAndDoesNotSpendIt(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var started = await Browser.StartAsync(host, "google");
        var callback = providers.Approve(started.Authorize, Accounts.Google());

        var (elsewhere, body) = await Browser.CallbackAsync(host, callback, cookie: null);
        var (owner, _) = await Browser.CallbackAsync(host, callback, started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, elsewhere);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.Single(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task AttackersCodeAndState_LandedInTheOwnersBrowser_AreRefused(Store store)
    {
        // Login CSRF: the attacker begins a sign-in with their own account and sends the owner's browser to the callback.
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var attacker = await Browser.StartAsync(host, "discord");
        var attackersCallback = providers.Approve(attacker.Authorize, Accounts.Discord());
        var owner = await Browser.StartAsync(host, "discord");

        var (status, body) = await Browser.CallbackAsync(host, attackersCallback, owner.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task AGoogleState_AtTheDiscordCallback_IsRefused(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var started = await Browser.StartAsync(host, "google");
        var googleCallback = providers.Approve(started.Authorize, Accounts.Google());

        var (status, body) = await Browser.CallbackAsync(host, new Uri(googleCallback.AbsoluteUri.Replace("/google/callback", "/discord/callback", StringComparison.Ordinal)), started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Fact]
    public async Task UnknownState_WithAMatchingCookie_IsRefused()
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);
        var made = "made-up-state-made-up-state-made-up-state-00";

        var (status, body) = await Browser.CallbackAsync(host,
            new Uri($"{SignInHost.PublicUrl}/api/auth/google/callback?code=x&state={made}"), "__Host-agentkit_signin=" + made);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("state_invalid", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Theory, InlineData(Store.Memory), InlineData(Store.Postgres)]
    public async Task ProviderSendingAnError_IsRefusedAsDeniedWithTheHostsContext(Store store)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, store);
        var started = await Browser.FollowStartAsync(host, HttpMethod.Post, "/setup?provider=google");

        var (status, body) = await Browser.CallbackAsync(host, FakeProviders.Deny(started.Authorize), started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("denied", Browser.Error(body));
        Assert.Equal("setup", body["purpose"]!.GetValue<string>());
        Assert.Empty(host.SignedIn);
    }
}
