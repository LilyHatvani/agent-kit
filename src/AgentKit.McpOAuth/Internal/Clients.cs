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
internal sealed class McpOAuthClients(McpOAuthOptions options, IHttpClientFactory http, ILogger<McpOAuthClients> log)
{
    private const int MaxDocumentBytes = 16 * 1024;

    public IReadOnlyList<string> Seeded => options.Clients.Select(c => c.ClientId).ToList();

    public IReadOnlyList<string> MetadataHosts => options.MetadataDocumentHosts.ToList();

    public async Task SeedAsync(IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        foreach (var client in options.Clients)
            await UpsertAsync(apps, client.ClientId, client.DisplayName, client.RedirectUris, ct);
    }

    /// <summary>A client id that names a metadata document this server would fetch: https, default port, an allowlisted host.</summary>
    public bool IsMetadataDocumentId(string? clientId, out Uri url)
    {
        url = null!;
        if (clientId is null || !Uri.TryCreate(clientId, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment)
            || u.AbsolutePath == "/" || !options.MetadataDocumentHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase))
            return false;
        url = u;
        return true;
    }

    /// <summary>
    /// Fetches a metadata document and creates or updates the client from it. A failed fetch leaves an earlier copy in
    /// place, so a client that connected before keeps working; a client never fetched stays unknown (invalid_client).
    /// </summary>
    public async Task EnsureMetadataClientAsync(IOpenIddictApplicationManager apps, string clientId, CancellationToken ct)
    {
        if (!IsMetadataDocumentId(clientId, out var url)) return;
        var doc = await FetchAsync(url, ct);
        if (doc is null) return;
        try
        {
            await UpsertAsync(apps, clientId, doc.Value.Name, doc.Value.Redirects, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Two first requests racing to create the same client: the unique ClientId index lets one win.
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
            var sameOrigin = u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.Equals(u.Host, url.Host, StringComparison.OrdinalIgnoreCase);
            if (!sameOrigin && !IsLoopback(u))
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

    /// <summary>An http loopback redirect (127.0.0.1, [::1] or localhost), which matches on any port (RFC 8252 § 7.3).</summary>
    public static bool IsLoopback(Uri u) =>
        u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));

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
/// <c>127.0.0.1</c> stay distinct, and nothing but an http loopback address is affected.
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
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var requested) || !McpOAuthClients.IsLoopback(requested)) return false;

        foreach (var registered in await GetRedirectUrisAsync(application, cancellationToken))
        {
            if (Uri.TryCreate(registered, UriKind.Absolute, out var r) && McpOAuthClients.IsLoopback(r)
                && string.Equals(r.Host, requested.Host, StringComparison.OrdinalIgnoreCase)
                && r.AbsolutePath == requested.AbsolutePath
                && r.Query == requested.Query
                && string.IsNullOrEmpty(requested.Fragment))
                return true;
        }
        return false;
    }
}
