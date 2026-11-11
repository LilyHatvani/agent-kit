using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

[assembly: AssemblyFixture(typeof(AgentKit.ProviderSignIn.Tests.SignInDatabase))]

namespace AgentKit.ProviderSignIn.Tests;

/// <summary>Where a host keeps its sign-ins in flight.</summary>
public enum Store
{
    Memory,
    Postgres,
}

/// <summary>
/// A throwaway PostgreSQL database with the package's schema (applied through <see cref="ProviderSignInSchema.ReadScript"/>,
/// twice, as a host's migrator might). The admin connection comes from AGENTKIT_TEST_ADMIN_CONNECTION (default localhost
/// via pgpass).
/// </summary>
public sealed class SignInDatabase : IAsyncLifetime
{
    private readonly string _database = "agentkit_signin_test_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static string AdminConnection =>
        Environment.GetEnvironmentVariable("AGENTKIT_TEST_ADMIN_CONNECTION") ?? "Host=localhost;Username=postgres;Database=postgres";

    public string ConnectionString => new NpgsqlConnectionStringBuilder(AdminConnection) { Database = _database }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await AdminExec($"CREATE DATABASE {_database}");
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < 2; i++)
        {
            await using var cmd = new NpgsqlCommand(ProviderSignInSchema.ReadScript(), conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await AdminExec($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    private static async Task AdminExec(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>A clock the test moves: state expiry is read from it. It starts at the real time.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A minimal product on the package, as Relay would be: both providers configured against the stubs, the package's
/// endpoints mapped, a completion that answers with what it was handed (and remembers it), and a host-begun "setup" flow
/// behind <c>POST /setup</c>.
/// </summary>
public sealed class SignInHost : IAsyncDisposable
{
    public const string PublicUrl = "https://app.test";

    private readonly WebApplication _app;

    private SignInHost(WebApplication app, ConcurrentQueue<ProviderSignInSuccess> signedIn)
    {
        _app = app;
        SignedIn = signedIn;
    }

    /// <summary>Every identity the host was handed. A refused sign-in must leave nothing here.</summary>
    public ConcurrentQueue<ProviderSignInSuccess> SignedIn { get; }

    public static async Task<SignInHost> StartAsync(FakeProviders providers, SignInDatabase database, Store store, TestClock? clock = null,
        Action<ProviderSignInOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (clock is not null) builder.Services.AddSingleton<TimeProvider>(clock);

        builder.Services.AddProviderSignIn(o =>
        {
            o.PublicUrl = PublicUrl;
            o.ConnectionString = store == Store.Postgres ? database.ConnectionString : null;
            o.Google.ClientId = FakeProviders.GoogleClientId;
            o.Google.ClientSecret = FakeProviders.GoogleClientSecret;
            o.Google.AuthorizeEndpoint = FakeProviders.GoogleAuthorize;
            o.Google.TokenEndpoint = FakeProviders.GoogleToken;
            o.Google.JwksUri = FakeProviders.GoogleJwks;
            o.Discord.ClientId = FakeProviders.DiscordClientId;
            o.Discord.ClientSecret = FakeProviders.DiscordClientSecret;
            o.Discord.AuthorizeEndpoint = FakeProviders.DiscordAuthorize;
            o.Discord.TokenEndpoint = FakeProviders.DiscordToken;
            o.Discord.UserEndpoint = FakeProviders.DiscordUser;
            configure?.Invoke(o);
        });
        builder.Services.AddHttpClient(ProviderSignInDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubTransport(providers));
        var signedIn = new ConcurrentQueue<ProviderSignInSuccess>();
        builder.Services.AddSingleton<IProviderSignInCompletion>(new EchoCompletion(signedIn));

        var app = builder.Build();
        app.MapProviderSignIn();
        // The host's own entry to a sign-in with another purpose: Relay's setup checks its setup token here first.
        app.MapPost("/setup", async (string provider, HttpContext context, ProviderSignInFlow flow) =>
            Results.Redirect((await flow.BeginAsync(provider, context, new ProviderSignInContext("setup", "setup-data", "/welcome"))).AbsoluteUri));
        await app.StartAsync();
        return new SignInHost(app, signedIn);
    }

    /// <summary>A browser: no redirects followed, no cookies kept (the driver carries the one cookie that matters).</summary>
    public HttpClient Browser()
    {
        var http = _app.GetTestServer().CreateClient();
        http.BaseAddress = new Uri(PublicUrl);
        return http;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>The host's completion: answers 200 with the identity and context it was handed, or 400 with the failure code.</summary>
public sealed class EchoCompletion(ConcurrentQueue<ProviderSignInSuccess> signedIn) : IProviderSignInCompletion
{
    public ValueTask<IResult> SignedInAsync(ProviderSignInSuccess success, HttpContext context, CancellationToken cancellationToken)
    {
        signedIn.Enqueue(success);
        return ValueTask.FromResult(Results.Json(new
        {
            provider = success.Identity.Provider,
            accountId = success.Identity.AccountId,
            name = success.Identity.DisplayName,
            email = success.Identity.Email,
            emailVerified = success.Identity.EmailVerified,
            purpose = success.Context.Purpose,
            data = success.Context.Data,
            returnTo = success.Context.ReturnTo,
        }));
    }

    public ValueTask<IResult> FailedAsync(ProviderSignInFailure failure, HttpContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Results.Json(new { error = failure.Code, provider = failure.Provider, purpose = failure.Context?.Purpose }, statusCode: 400));
}

/// <summary>Drives a sign-in the way the owner's browser does.</summary>
public static class Browser
{
    /// <summary>A started sign-in: where the browser was sent, and the binding cookie it now carries (name=value).</summary>
    public sealed record Started(Uri Authorize, string Cookie, string SetCookie);

    public static Task<Started> StartAsync(SignInHost host, string provider, string? returnTo = null) =>
        FollowStartAsync(host, HttpMethod.Get, returnTo is null ? $"/api/auth/{provider}/start" : $"/api/auth/{provider}/start?returnTo={Uri.EscapeDataString(returnTo)}");

    public static async Task<Started> FollowStartAsync(SignInHost host, HttpMethod method, string path)
    {
        using var http = host.Browser();
        using var r = await http.SendAsync(new HttpRequestMessage(method, path));
        Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        var setCookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        return new Started(r.Headers.Location!, setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)], setCookie);
    }

    /// <summary>The browser arrives at the callback the provider sent it to, with <paramref name="cookie"/> (or none).</summary>
    public static async Task<(HttpStatusCode Status, JsonObject Body)> CallbackAsync(SignInHost host, Uri callback, string? cookie)
    {
        Assert.StartsWith(SignInHost.PublicUrl + "/api/auth/", callback.AbsoluteUri, StringComparison.Ordinal);
        using var http = host.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, callback.PathAndQuery);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        using var r = await http.SendAsync(request);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, text.Length > 0 ? JsonNode.Parse(text)!.AsObject() : []);
    }

    /// <summary>Start, approve as <paramref name="account"/> at the stub provider, come back.</summary>
    public static async Task<(HttpStatusCode Status, JsonObject Body)> SignInAsync(SignInHost host, FakeProviders providers, string provider,
        StubAccount account, IdTokenTweak? tweak = null)
    {
        var started = await StartAsync(host, provider);
        return await CallbackAsync(host, providers.Approve(started.Authorize, account, tweak), started.Cookie);
    }

    public static string Error(JsonObject body) => body["error"]?.GetValue<string>() ?? $"(no error: {body.ToJsonString()})";
}

/// <summary>Stub accounts.</summary>
public static class Accounts
{
    public static StubAccount Google() =>
        new("1" + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999) + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999), "Ada Lovelace", "ada@example.com");

    public static StubAccount Discord() =>
        new("8" + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999) + RandomNumberGenerator.GetInt32(10_000_000, 99_999_999), "Grace Hopper", null);
}
