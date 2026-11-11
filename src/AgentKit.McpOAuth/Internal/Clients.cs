using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AgentKit.McpOAuth.Internal;

/// <summary>
/// Which clients may connect: the seeded ones (<see cref="McpOAuthOptions.Clients"/>) and client id metadata documents
/// on an allowlisted host (<see cref="McpOAuthOptions.MetadataDocumentHosts"/>). No dynamic registration.
/// </summary>
internal sealed class McpOAuthClients(McpOAuthOptions options, IHttpClientFactory http, TimeProvider clock, ILogger<McpOAuthClients> log)
{
    private const int MaxDocumentBytes = 16 * 1024;

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "[::1]", "localhost"];

    private readonly RecentDocuments _documents = new(options.MetadataDocumentCacheSize);

    public IReadOnlyList<string> Seeded => options.Clients.Select(c => c.ClientId).ToList();

    public IReadOnlyList<string> MetadataHosts => options.MetadataDocumentHosts.ToList();

    public async Task SeedAsync(IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        foreach (var client in options.Clients)
            await UpsertAsync(apps, client.ClientId, client.DisplayName, client.RedirectUris, ct);
    }

    /// <summary>
    /// A client id that names a metadata document this server would fetch: https, default port, an allowlisted host, and
    /// written canonically (<c>clientId == url.AbsoluteUri</c>). An alias (dot segments, case, <c>:443</c>) names the
    /// same document but would register a client of its own, so it is not one.
    /// </summary>
    public bool IsMetadataDocumentId(string? clientId, out Uri url)
    {
        url = null!;
        if (clientId is null || !Uri.TryCreate(clientId, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment)
            || u.AbsolutePath == "/" || !options.MetadataDocumentHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase)
            || !string.Equals(u.AbsoluteUri, clientId, StringComparison.Ordinal))
            return false;
        url = u;
        return true;
    }

    /// <summary>
    /// A client id written as a URL but not canonically: refused outright at the authorize endpoint, so a client row an
    /// earlier version stored under an alias is never used either.
    /// </summary>
    public static bool IsNonCanonicalUrlClientId(string clientId) =>
        clientId.Contains("://", StringComparison.Ordinal)
        && (!Uri.TryCreate(clientId, UriKind.Absolute, out var u) || !string.Equals(u.AbsoluteUri, clientId, StringComparison.Ordinal));

    /// <summary>
    /// Fetches a metadata document and creates or updates the client from it, at most once per
    /// <see cref="McpOAuthOptions.MetadataDocumentCacheLifetime"/> (a document stored) or
    /// <see cref="McpOAuthOptions.MetadataDocumentFailureCacheLifetime"/> (one missing or refused). A failed fetch leaves
    /// an earlier copy in place, so a client that connected before keeps working; a client never fetched stays unknown
    /// (invalid_client).
    /// </summary>
    public async Task EnsureMetadataClientAsync(IOpenIddictApplicationManager apps, string clientId, CancellationToken ct)
    {
        if (!IsMetadataDocumentId(clientId, out var url)) return;
        if (_documents.IsFresh(clientId, clock.GetUtcNow())) return;
        var doc = await FetchAsync(url, ct);
        if (doc is null)
        {
            _documents.Remember(clientId, clock.GetUtcNow(), options.MetadataDocumentFailureCacheLifetime);
            return;
        }
        try
        {
            await UpsertAsync(apps, clientId, doc.Value.Name, doc.Value.Redirects, ct);
            _documents.Remember(clientId, clock.GetUtcNow(), options.MetadataDocumentCacheLifetime);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Two first requests racing to create the same client: the unique ClientId index lets one win. Not remembered,
            // so the next request stores it again if this was anything else.
            log.LogWarning(e, "Storing the client metadata document {ClientId} failed.", clientId);
        }
    }

    private async Task<(string Name, string[] Redirects)?> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(McpOAuthDefaults.MetadataHttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Client metadata document {Url} answered {Status}.", url, (int)response.StatusCode);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MaxDocumentBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token)) > 0) read += n;
            if (read > MaxDocumentBytes)
            {
                log.LogWarning("Client metadata document {Url} is over {Max} bytes.", url, MaxDocumentBytes);
                return null;
            }
            return Parse(url, buffer.AsSpan(0, read));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Fetching the client metadata document {Url} failed.", url);
            return null;
        }
    }

    /// <summary>
    /// A document is accepted when its <c>client_id</c> equals its URL and every redirect is on the document's own origin
    /// over https, or an http loopback address (native apps, RFC 8252).
    /// </summary>
    private (string Name, string[] Redirects)? Parse(Uri url, ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() != url.AbsoluteUri)
        {
            log.LogWarning("Client metadata document {Url} does not name itself as client_id.", url);
            return null;
        }
        if (!root.TryGetProperty("redirect_uris", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var redirects = new List<string>();
        foreach (var r in list.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.String || r.GetString() is not { } s || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
            var sameOrigin = u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.Equals(u.Host, url.Host, StringComparison.OrdinalIgnoreCase)
                             && string.IsNullOrEmpty(u.UserInfo) && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
            if (!sameOrigin && !IsLoopback(s))
            {
                log.LogWarning("Client metadata document {Url} names a redirect {Redirect} outside its origin.", url, s);
                return null;
            }
            redirects.Add(s);
        }
        if (redirects.Count == 0) return null;
        var name = root.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String
                   && n.GetString() is { Length: > 0 and <= 60 } cn ? cn : url.Host;
        return (name, redirects.ToArray());
    }

    /// <summary>An http loopback redirect, which matches on any port (RFC 8252 § 7.3). See <see cref="LoopbackHost"/>.</summary>
    public static bool IsLoopback(string? redirect) => LoopbackHost(redirect) is not null;

    /// <summary>
    /// The loopback host of an http redirect as written (<c>127.0.0.1</c>, <c>[::1]</c> or <c>localhost</c>, lower case),
    /// or null. Decided on the text the browser will follow, not on what <see cref="Uri"/> makes of it: .NET reads
    /// <c>loopback</c> as <c>localhost</c> and <c>127.1</c> as <c>127.0.0.1</c>, a browser looks the first up, and
    /// <see cref="Uri"/> trims padding a browser may not. So: no whitespace or control characters, no userinfo, the host
    /// one of the three literally (RFC 8252 § 8.3), a numeric port or none, and .NET's reading of it the same host.
    /// </summary>
    public static string? LoopbackHost(string? redirect)
    {
        const string Prefix = "http://";
        if (string.IsNullOrEmpty(redirect) || !redirect.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || redirect.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(redirect, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp || u.UserInfo.Length > 0)
            return null;

        var rest = redirect.AsSpan(Prefix.Length);
        var end = rest.IndexOfAny("/?#\\");
        var authority = end >= 0 ? rest[..end] : rest;
        if (authority.Contains('@')) return null;
        int hostEnd;
        if (authority.StartsWith("["))
        {
            hostEnd = authority.IndexOf(']') + 1;
            if (hostEnd == 0) return null;
        }
        else hostEnd = authority.IndexOf(':') is var colon and >= 0 ? colon : authority.Length;
        var port = authority[hostEnd..];
        if (port.Length > 0 && (port[0] != ':' || port[1..].IndexOfAnyExceptInRange('0', '9') >= 0)) return null;

        var host = authority[..hostEnd].ToString();
        var literal = LoopbackHosts.FirstOrDefault(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        return literal is not null && string.Equals(u.Host, literal, StringComparison.OrdinalIgnoreCase) ? literal : null;
    }

    private async Task UpsertAsync(IOpenIddictApplicationManager apps, string clientId, string name, IEnumerable<string> redirects, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit,
            DisplayName = name,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + options.Scope,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var r in redirects) descriptor.RedirectUris.Add(new Uri(r));

        var existing = await apps.FindByClientIdAsync(clientId, ct);
        if (existing is null) await apps.CreateAsync(descriptor, ct);
        else await apps.UpdateAsync(existing, descriptor, ct);
    }
}

/// <summary>
/// The metadata documents fetched lately, found or not, each until its expiry. Bounded: when full, expired entries go
/// first, then the oldest. Per process; a replica fetches for itself.
/// </summary>
internal sealed class RecentDocuments(int capacity)
{
    private readonly Dictionary<string, (DateTimeOffset Expires, long Seq)> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _seq;

    public bool IsFresh(string clientId, DateTimeOffset now)
    {
        lock (_lock) return _entries.TryGetValue(clientId, out var e) && e.Expires > now;
    }

    public void Remember(string clientId, DateTimeOffset now, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero) return;
        lock (_lock)
        {
            if (!_entries.ContainsKey(clientId) && _entries.Count >= capacity)
            {
                foreach (var stale in _entries.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                    _entries.Remove(stale);
                if (_entries.Count >= capacity) _entries.Remove(_entries.MinBy(kv => kv.Value.Seq).Key);
            }
            _entries[clientId] = (now + lifetime, ++_seq);
        }
    }
}

/// <summary>Creates or refreshes the seeded clients at start.</summary>
internal sealed class McpOAuthSeeder(IServiceScopeFactory scopes, McpOAuthClients clients) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await clients.SeedAsync(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// OpenIddict's application manager with one change: a loopback redirect matches a registered one on any port
/// (RFC 8252 § 7.3). Native clients bind an ephemeral port per sign-in and register <c>http://localhost/callback</c>;
/// OpenIddict compares redirects exactly. Scheme, host, path and query must still match, so <c>localhost</c> and
/// <c>127.0.0.1</c> stay distinct, and nothing but an http loopback address written literally is affected
/// (<see cref="McpOAuthClients.LoopbackHost"/>): <c>http://loopback:8080/callback</c> is refused.
/// </summary>
internal sealed class LoopbackApplicationManager<TApplication>(
    IOpenIddictApplicationCache<TApplication> cache,
    ILogger<OpenIddictApplicationManager<TApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<TApplication> store)
    : OpenIddictApplicationManager<TApplication>(cache, logger, options, store)
    where TApplication : class
{
    public override async ValueTask<bool> ValidateRedirectUriAsync(TApplication application, string uri, CancellationToken cancellationToken = default)
    {
        if (await base.ValidateRedirectUriAsync(application, uri, cancellationToken)) return true;
        if (McpOAuthClients.LoopbackHost(uri) is not { } host || !Uri.TryCreate(uri, UriKind.Absolute, out var requested)) return false;

        foreach (var registered in await GetRedirectUrisAsync(application, cancellationToken))
        {
            if (McpOAuthClients.LoopbackHost(registered) == host && Uri.TryCreate(registered, UriKind.Absolute, out var r)
                && r.AbsolutePath == requested.AbsolutePath
                && r.Query == requested.Query
                && string.IsNullOrEmpty(requested.Fragment))
                return true;
        }
        return false;
    }
}
