using System.Net;

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>
/// What the provider answers must prove an account for this very sign-in: Google's ID token is checked for signature,
/// issuer, audience, expiry and nonce; Discord's user must carry a snowflake id. Anything else is refused and the host
/// never sees an identity.
/// </summary>
public sealed class ProviderAnswerTests(SignInDatabase database)
{
    public static TheoryData<string, IdTokenTweak> BadIdTokens => new()
    {
        { "wrong nonce", new IdTokenTweak(Nonce: "another-sign-ins-nonce-another-sign-ins-nonce") },
        { "no nonce", new IdTokenTweak(OmitNonce: true) },
        { "wrong audience", new IdTokenTweak(Audience: "someone-elses-client.apps.googleusercontent.com") },
        { "wrong issuer", new IdTokenTweak(Issuer: "https://accounts.google.test") },
        { "forged signature", new IdTokenTweak(ForgedSignature: true) },
        { "expired", new IdTokenTweak(Expired: true) },
        { "no ID token", new IdTokenTweak(OmitIdToken: true) },
    };

    [Theory, MemberData(nameof(BadIdTokens))]
    public async Task GoogleIdToken_ThatDoesNotProveThisSignIn_IsRefused(string why, IdTokenTweak tweak)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var (status, body) = await Browser.SignInAsync(host, providers, "google", Accounts.Google(), tweak);

        Assert.True(status == HttpStatusCode.BadRequest, why);
        Assert.Equal("identity_rejected", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Fact]
    public async Task GoogleIdToken_GenuineAfterAForgedOne_StillSignsIn()
    {
        // The forged token names the genuine key id; refusing it must not poison the key cache for the next sign-in.
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);
        await Browser.SignInAsync(host, providers, "google", Accounts.Google(), new IdTokenTweak(ForgedSignature: true));

        var (status, _) = await Browser.SignInAsync(host, providers, "google", Accounts.Google());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Single(host.SignedIn);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task DiscordUser_WithoutASnowflakeId_IsRefused(string id)
    {
        using var providers = new FakeProviders { DiscordIdOverride = id };
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var (status, body) = await Browser.SignInAsync(host, providers, "discord", Accounts.Discord());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("identity_rejected", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Theory, InlineData("google"), InlineData("discord")]
    public async Task TokenEndpoint_InTrouble_IsReportedAsProviderUnavailable(string provider)
    {
        using var providers = new FakeProviders { TokenEndpointFailure = HttpStatusCode.ServiceUnavailable };
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);

        var (status, body) = await Browser.SignInAsync(host, providers, provider, provider == "google" ? Accounts.Google() : Accounts.Discord());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("provider_unavailable", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }

    [Theory, InlineData("google"), InlineData("discord")]
    public async Task ACodeTheProviderNeverIssued_IsRefusedAsAFailedExchange(string provider)
    {
        using var providers = new FakeProviders();
        await using var host = await SignInHost.StartAsync(providers, database, Store.Memory);
        var started = await Browser.StartAsync(host, provider);
        var genuine = providers.Approve(started.Authorize, provider == "google" ? Accounts.Google() : Accounts.Discord());

        var forgedCode = new Uri(genuine.AbsoluteUri.Replace("code=code-", "code=forged-", StringComparison.Ordinal));
        var (status, body) = await Browser.CallbackAsync(host, forgedCode, started.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("exchange_failed", Browser.Error(body));
        Assert.Empty(host.SignedIn);
    }
}
