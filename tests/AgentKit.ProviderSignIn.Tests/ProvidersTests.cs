using System.Net;
using System.Text.Json.Nodes;

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>A provider is offered, startable and completable only when its client id and secret are configured.</summary>
public sealed class ProvidersTests(SignInDatabase database)
{
    [Fact]
    public async Task BothConfigured_BothAreOffered()
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        Assert.Equal(["google", "discord"], await OfferedAsync(host));
    }

    [Fact]
    public async Task DiscordWithoutCredentials_IsNotOfferedNorStartableNorCompletable()
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory, configure: o =>
        {
            o.Discord.ClientId = null;
            o.Discord.ClientSecret = null;
        });
        using var http = host.Browser();

        Assert.Equal(["google"], await OfferedAsync(host));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/discord/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/discord/callback?code=x&state=y")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Browser.SignInAsync(host, providers, "google", Accounts.Google())).Status);
    }

    [Fact]
    public async Task NoneConfigured_NothingIsOfferedAndNoSignInStarts()
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory, configure: o =>
        {
            o.PublicUrl = null;
            o.Google.ClientId = o.Google.ClientSecret = null;
            o.Discord.ClientId = o.Discord.ClientSecret = null;
        });
        using var http = host.Browser();

        Assert.Empty(await OfferedAsync(host));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/google/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/discord/start")).StatusCode);
    }

    [Fact]
    public async Task UnknownProvider_IsNotStartable()
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);
        using var http = host.Browser();

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/github/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/auth/Google/start")).StatusCode);
    }

    [Fact]
    public async Task AProviderWithoutAPublicUrl_IsRefusedAtStartup()
    {
        using var providers = new FakeProviders();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SignInHost.StartAsync(providers, database, Store.Memory, configure: o => o.PublicUrl = null));
    }

    [Fact]
    public async Task HalfAClient_IsRefusedAtStartup()
    {
        using var providers = new FakeProviders();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SignInHost.StartAsync(providers, database, Store.Memory, configure: o => o.Google.ClientSecret = null));
    }

    private static async Task<string[]> OfferedAsync(SignInHost host)
    {
        using var http = host.Browser();
        var body = JsonNode.Parse(await http.GetStringAsync("/api/auth/providers"))!;
        return [.. body["providers"]!.AsArray().Select(p => p!.GetValue<string>())];
    }
}
