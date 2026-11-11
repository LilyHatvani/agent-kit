using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

[assembly: AssemblyFixture(typeof(AgentKit.McpOAuth.Tests.OAuthApp))]

namespace AgentKit.McpOAuth.Tests;

/// <summary>
/// A throwaway PostgreSQL database with the package's schema (applied through <see cref="McpOAuthSchema.ReadScript"/>,
/// twice, as a host's migrator might), and a host using the package as a product would. The admin connection comes from
/// AGENTKIT_TEST_ADMIN_CONNECTION (default localhost via pgpass). The host's public origin is <see cref="PublicUrl"/>,
/// not the TestServer's http://localhost, so every OAuth URL checked comes from configuration (issuer pinning).
/// </summary>
public sealed class OAuthApp : IAsyncLifetime
{
    public const string PublicUrl = "https://mcp.test";
    public const string Resource = PublicUrl + "/mcp";
    public const string Scope = "tasks";

    private readonly string _database = "agentkit_test_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static string AdminConnection =>
        Environment.GetEnvironmentVariable("AGENTKIT_TEST_ADMIN_CONNECTION") ?? "Host=localhost;Username=postgres;Database=postgres";

    public string ConnectionString => new NpgsqlConnectionStringBuilder(AdminConnection) { Database = _database }.ConnectionString;

    /// <summary>The owner secret the main host's consent page asks for.</summary>
    public string OwnerSecret { get; } = "owner-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    /// <summary>The host's connections, shared by every host on this database (replicas see the same store).</summary>
    public ConnectionBook Book { get; } = new();

    /// <summary>What the fake web serves to metadata document fetches: URL → JSON.</summary>
    public ConcurrentDictionary<string, string> MetadataDocuments { get; } = new();

    /// <summary>How often the fake web was asked for each URL, by every host on this database.</summary>
    public ConcurrentDictionary<string, int> Fetches { get; } = new();

    public int FetchCount(string url) => Fetches.GetValueOrDefault(url);

    public McpHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await AdminExec($"CREATE DATABASE {_database}");
        await using (var conn = new NpgsqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            for (var i = 0; i < 2; i++)
            {
                await using var cmd = new NpgsqlCommand(McpOAuthSchema.ReadScript(), conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        Host = await StartHostAsync(PublicUrl);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await AdminExec($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    /// <summary>Another host process on the same database: a replica, another origin, or the server off (null).</summary>
    public Task<McpHost> StartHostAsync(string? publicUrl, IMcpConsentOwner? owner = null, Action<McpOAuthOptions>? options = null,
        TimeProvider? clock = null) =>
        McpHost.StartAsync(publicUrl, ConnectionString, Book, owner ?? new McpSecretOwnerCheck(() => OwnerSecret),
            new FakeWeb(MetadataDocuments, Fetches), options, clock);

    private static async Task AdminExec(string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A minimal product on the package: <c>/mcp</c> answers with who is calling, <c>DELETE /connections/{id}</c> revokes,
/// <see cref="ConnectionBook"/> keeps the connections.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private McpHost(WebApplication app) => _app = app;

    public static async Task<McpHost> StartAsync(string? publicUrl, string connectionString, ConnectionBook book, IMcpConsentOwner owner,
        FakeWeb web, Action<McpOAuthOptions>? configure = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddMcpOAuth(o =>
        {
            o.PublicUrl = publicUrl;
            o.ConnectionString = connectionString;
            o.Scope = OAuthApp.Scope;
            o.ResourceName = "Test Host";
            o.ConsentDescription = "It will read and write tasks as the agent you pick.";
            o.Clients.Add(McpOAuthClient.ClaudeAi);
            o.Clients.Add(McpOAuthClient.ClaudeCode);
            o.MetadataDocumentHosts.Add("claude.ai");
            o.MetadataDocumentHosts.Add("chatgpt.com");
            configure?.Invoke(o);
        });
        if (clock is not null) builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<IMcpConnectionStore>(new BookStore(book));
        builder.Services.AddSingleton(owner);
        // Metadata documents come from the fake web, never the network.
        builder.Services.AddHttpClient(McpOAuthDefaults.MetadataHttpClientName).ConfigurePrimaryHttpMessageHandler(() => web);

        var app = builder.Build();
        app.UseMcpOAuth();
        app.UseAuthentication();
        app.UseMcpOAuthGate();
        app.MapMcpOAuth();
        app.MapPost("/mcp", (HttpContext c) => Results.Json(new Dictionary<string, string?>
        {
            ["connection"] = c.User.FindFirstValue(BookStore.ConnectionClaim),
            ["agent"] = c.User.FindFirstValue(BookStore.AgentClaim),
            ["client"] = c.User.FindFirstValue(BookStore.ClientClaim),
        }));
        app.MapDelete("/connections/{id:guid}", async (Guid id, IMcpOAuthGrants grants, CancellationToken ct) =>
        {
            if (book.Revoke(id) is not { } revoked) return Results.NotFound();
            if (revoked.AuthorizationId is { } authorization) await grants.RevokeAsync(authorization, ct);
            return Results.NoContent();
        });
        await app.StartAsync();
        return new McpHost(app);
    }

    /// <summary>A browser: no redirects followed; it keeps the cookies this host sets, and sends <paramref name="cookie"/> too.</summary>
    public HttpClient Browser(string? cookie = null)
    {
        var server = _app.GetTestServer();
        var http = new HttpClient(new CookieJar(server.CreateHandler())) { BaseAddress = server.BaseAddress };
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/json");
        if (cookie is not null) http.DefaultRequestHeaders.Add("Cookie", cookie);
        return http;
    }

    /// <summary>A call on <c>/mcp</c> with this bearer.</summary>
    public async Task<HttpResponseMessage> McpAsync(string? bearer)
    {
        using var http = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return await http.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record Connection(Guid Id, string Agent, string Label, string? AuthorizationId, string? ClientId, string? OwnerId, bool Revoked);

/// <summary>The host's connections: OAuth ones created on consent, and static tokens it mints itself.</summary>
public sealed class ConnectionBook
{
    private readonly ConcurrentDictionary<Guid, Connection> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _static = new();

    public IReadOnlyCollection<Connection> All => _byId.Values.ToList();

    public Connection? Find(Guid id) => _byId.GetValueOrDefault(id);

    public Connection? FindByLabel(string label) => _byId.Values.SingleOrDefault(c => c.Label == label);

    public void Add(Connection c)
    {
        if (!_byId.TryAdd(c.Id, c)) throw new InvalidOperationException("Duplicate connection.");
    }

    public string MintStatic(string agent)
    {
        var secret = "static_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var id = Guid.CreateVersion7();
        Add(new Connection(id, agent, "static " + id, null, null, null, false));
        _static[secret] = id;
        return secret;
    }

    public Guid? StaticId(string secret) => _static.TryGetValue(secret, out var id) ? id : null;

    public Connection? Revoke(Guid id)
    {
        if (!_byId.TryGetValue(id, out var c) || c.Revoked) return null;
        var revoked = c with { Revoked = true };
        _byId[id] = revoked;
        return revoked;
    }
}

/// <summary>The host's <see cref="IMcpConnectionStore"/>: an agent and a label per connection, read on every call.</summary>
public sealed class BookStore(ConnectionBook book) : IMcpConnectionStore
{
    public const string ConnectionClaim = "connection";
    public const string AgentClaim = "agent";
    public const string ClientClaim = "client";
    public static readonly string[] Agents = ["claude", "chatgpt"];

    public ValueTask<string> RenderChoicesAsync(McpConsentRequest request, IFormCollection? submitted, CancellationToken cancellationToken)
    {
        var suggested = submitted?["agent"].ToString() is { Length: > 0 } s ? s
            : request.MetadataDocument?.Host == "chatgpt.com" ? "chatgpt" : "claude";
        var label = submitted?["label"].ToString() is { Length: > 0 } l ? l : $"{request.ClientName} — {request.Now:yyyy-MM-dd}";
        var options = string.Concat(Agents.Select(a => $"<option value=\"{a}\"{(a == suggested ? " selected" : "")}>{a}</option>"));
        return ValueTask.FromResult($"""
            <label for="agent">Works as</label>
            <select id="agent" name="agent">{options}</select>
            <label for="label">Label</label>
            <input id="label" name="label" value="{WebUtility.HtmlEncode(label)}">
            """);
    }

    public ValueTask CreateAsync(McpConsentRequest request, McpOAuthGrant grant, string ownerId, IFormCollection form, CancellationToken cancellationToken)
    {
        var agent = form["agent"].ToString();
        if (!Agents.Contains(agent)) throw new McpConsentRejectedException($"No agent '{agent}'.");
        var label = form["label"].ToString().Trim();
        if (label.Length == 0) throw new McpConsentRejectedException("A label is required.");
        book.Add(new Connection(grant.ConnectionId, agent, label, grant.AuthorizationId, grant.ClientId, ownerId, false));
        return ValueTask.CompletedTask;
    }

    public ValueTask<ClaimsPrincipal?> ResolveAsync(McpOAuthGrant grant, CancellationToken cancellationToken)
    {
        var c = book.Find(grant.ConnectionId);
        return ValueTask.FromResult(c is { Revoked: false } && c.AuthorizationId == grant.AuthorizationId ? Principal(c, grant.ClientId) : null);
    }

    public ValueTask<ClaimsPrincipal?> ResolveOtherBearerAsync(string bearer, CancellationToken cancellationToken)
    {
        if (!bearer.StartsWith("static_", StringComparison.Ordinal) || book.StaticId(bearer) is not { } id) return ValueTask.FromResult<ClaimsPrincipal?>(null);
        var c = book.Find(id);
        return ValueTask.FromResult(c is { Revoked: false } ? Principal(c, "static") : null);
    }

    private static ClaimsPrincipal Principal(Connection c, string client) => new(new ClaimsIdentity(
    [
        new Claim(ConnectionClaim, c.Id.ToString()),
        new Claim(AgentClaim, c.Agent),
        new Claim(ClientClaim, client),
    ], "mcp"));
}

/// <summary>An owner recognised by a session cookie, as owner sign-in would: no credential field, a sign-in redirect otherwise.</summary>
public sealed class CookieOwner : IMcpConsentOwner
{
    public ValueTask<McpOwnerCheck> CheckAsync(McpConsentRequest request, HttpContext context, IFormCollection? form, CancellationToken cancellationToken) =>
        ValueTask.FromResult(context.Request.Cookies["owner_session"] is { Length: > 0 } who
            ? McpOwnerCheck.Owner(who)
            : McpOwnerCheck.SignIn("/signin?return=" + Uri.EscapeDataString(request.ReturnUrl)));
}

/// <summary>The outside web as the metadata fetch sees it: a fixed table of documents, 404 otherwise. It counts every request.</summary>
public sealed class FakeWeb(ConcurrentDictionary<string, string> documents, ConcurrentDictionary<string, int> fetches) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        fetches.AddOrUpdate(url, 1, (_, n) => n + 1);
        return Task.FromResult(documents.TryGetValue(url, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// A browser's cookie store, reduced to what these tests need: it keeps what the server sets and sends it back, Secure
/// or not (TestServer's origin is http), next to any Cookie header the request already carries.
/// </summary>
public sealed class CookieJar(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, string> _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_cookies.IsEmpty)
        {
            var mine = string.Join("; ", _cookies.Select(kv => kv.Key + "=" + kv.Value));
            var existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", existing is null ? mine : existing + "; " + mine);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
            foreach (var line in set)
            {
                var pair = line.Split(';', 2)[0];
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0) continue;
                var (name, value) = (pair[..eq].Trim(), pair[(eq + 1)..].Trim());
                if (value.Length == 0 || line.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)) _cookies.TryRemove(name, out _);
                else _cookies[name] = value;
            }
        return response;
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
